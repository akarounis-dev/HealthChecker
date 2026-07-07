using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HealthChecker.Clients;

static class ArgocdClient
{
    /// <summary>
    /// Resolves an ArgoCD token from (in priority order): env var, config file, ArgoCD CLI config.
    /// Returns (token, null) on success, or (null, message) with user-facing instructions when
    /// no token is found. The caller decides how to display the message.
    /// </summary>
    public static async Task<(string? Token, string? NoTokenMessage)> ResolveTokenAsync(
        string serverHost, string? configToken = null)
    {
        var envToken = Environment.GetEnvironmentVariable("ARGOCD_TOKEN");
        if (!string.IsNullOrWhiteSpace(envToken))
            return (envToken, null);

        if (!string.IsNullOrWhiteSpace(configToken))
            return (configToken, null);

        var cliToken = await ReadFromArgoCdCliConfigAsync(serverHost);
        if (cliToken is not null)
            return (cliToken, null);

        var message =
            $"No ArgoCD token found. To enable pod-level checks:\n" +
            $"  1. Open https://{serverHost} in your browser\n" +
            $"  2. DevTools → Network → refresh → click any /api/v1/ request\n" +
            $"  3. Request Headers → copy the value after  argocd.token=\n" +
            $"  4. Paste it into  argocd_token  in your config file";

        return (null, message);
    }

    public static TimeSpan? GetTokenTimeLeft(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 3) return null;   // JWT must have exactly 3 segments

            var payload = parts[1];
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
                             .Replace('-', '+').Replace('_', '/');

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var exp  = JsonNode.Parse(json)?["exp"]?.GetValue<long>();
            if (exp is null) return null;

            return DateTimeOffset.FromUnixTimeSeconds(exp.Value) - DateTimeOffset.UtcNow;
        }
        catch { return null; }
    }

    public static async Task<(string Status, string[] DegradedResources, int PodCount)> GetAppHealth(
        string serverUrl, string token, string appName, int timeoutSec = 10,
        HttpClient? http = null, HttpMessageHandler? handler = null)
    {
        HttpClient? owned = null;
        http ??= owned = handler is not null
            ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(timeoutSec) }
            : new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };

        try
        {
            var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{serverUrl.TrimEnd('/')}/api/v1/applications/{Uri.EscapeDataString(appName)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            HttpResponseMessage response;
            try { response = await http.SendAsync(request); }
            catch (Exception ex) { return ("Unknown", [$"Request failed: {ex.Message}"], 0); }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                try
                {
                    var msg = JsonNode.Parse(body)?["message"]?.GetValue<string>();
                    if (msg is not null) return ("Unknown", [$"ArgoCD: {msg}"], 0);
                }
                catch { /* ignore */ }
                return ("Unknown", [$"HTTP {(int)response.StatusCode} {response.ReasonPhrase}"], 0);
            }

            var json = await response.Content.ReadAsStringAsync();
            JsonNode? doc;
            try { doc = JsonNode.Parse(json); }
            catch { return ("Unknown", ["Failed to parse ArgoCD response"], 0); }

            var status = doc?["status"]?["health"]?["status"]?.GetValue<string>() ?? "Unknown";

            var resources = doc?["status"]?["resources"]?.AsArray() ?? [];

            var degraded = resources
                .Where(r =>
                {
                    var h = r?["health"]?["status"]?.GetValue<string>();
                    return h is not null && h != "Healthy" && h != "Missing";
                })
                .Select(r =>
                    $"{r?["kind"]?.GetValue<string>() ?? "?"}/{r?["name"]?.GetValue<string>() ?? "?"} " +
                    $"[{r?["health"]?["status"]?.GetValue<string>() ?? "?"}]")
                .ToArray();

            var podCount = resources.Count(r => r?["kind"]?.GetValue<string>() == "Pod");

            // If pods are excluded from status.resources (common ArgoCD config), fall back to resource-tree
            if (podCount == 0)
            {
                try
                {
                    var treeReq = new HttpRequestMessage(
                        HttpMethod.Get,
                        $"{serverUrl.TrimEnd('/')}/api/v1/applications/{Uri.EscapeDataString(appName)}/resource-tree");
                    treeReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var treeResp = await http.SendAsync(treeReq);
                    if (treeResp.IsSuccessStatusCode)
                    {
                        var treeJson = await treeResp.Content.ReadAsStringAsync();
                        podCount = JsonNode.Parse(treeJson)?["nodes"]?.AsArray()
                            .Count(n => n?["kind"]?.GetValue<string>() == "Pod") ?? 0;
                    }
                }
                catch { /* ignore */ }
            }

            return (status, degraded, podCount);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>
    /// Queries the ArgoCD resource tree for an Ingress or IngressRoute and returns its host URL.
    /// Returns (null, reason) when no ingress is found or the request fails.
    /// </summary>
    public static async Task<(string? Url, string? Reason)> GetAppIngress(
        string serverUrl, string token, string appName, int timeoutSec = 10,
        HttpClient? http = null, HttpMessageHandler? handler = null)
    {
        HttpClient? owned = null;
        http ??= owned = handler is not null
            ? new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(timeoutSec) }
            : new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSec) };

        // Only set auth on owned clients; shared clients have auth set by the caller.
        if (owned is not null)
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            // Step 1 - resource tree to find an Ingress node
            var treeJson = await http.GetStringAsync(
                $"{serverUrl.TrimEnd('/')}/api/v1/applications/{Uri.EscapeDataString(appName)}/resource-tree");
            var treeDoc = JsonNode.Parse(treeJson);

            var nodes = treeDoc?["nodes"]?.AsArray() ?? [];

            // Support both standard k8s Ingress and Traefik IngressRoute CRD
            var ingressNode = nodes.FirstOrDefault(n =>
                n?["kind"]?.GetValue<string>() is "Ingress" or "IngressRoute");

            if (ingressNode is null)
            {
                var kinds = string.Join(", ", nodes
                    .Select(n => n?["kind"]?.GetValue<string>())
                    .Where(k => k is not null)
                    .Distinct()
                    .OrderBy(k => k));
                return (null, $"No Ingress/IngressRoute in resource tree (found: {kinds})");
            }

            var kind         = ingressNode["kind"]?    .GetValue<string>() ?? "Ingress";
            var resourceName = ingressNode["name"]?    .GetValue<string>();
            var ns           = ingressNode["namespace"]?.GetValue<string>() ?? "";
            var group        = ingressNode["group"]?   .GetValue<string>() ?? "networking.k8s.io";
            var version      = ingressNode["version"]? .GetValue<string>() ?? "v1";
            if (resourceName is null) return (null, $"{kind} node has no name");

            // Step 2 - fetch the resource manifest
            var resourceUrl =
                $"{serverUrl.TrimEnd('/')}/api/v1/applications/{Uri.EscapeDataString(appName)}/resource" +
                $"?resourceName={Uri.EscapeDataString(resourceName)}" +
                $"&namespace={Uri.EscapeDataString(ns)}" +
                $"&version={Uri.EscapeDataString(version)}" +
                $"&kind={Uri.EscapeDataString(kind)}" +
                $"&group={Uri.EscapeDataString(group)}";

            var resourceJson = await http.GetStringAsync(resourceUrl);
            var manifestStr  = JsonNode.Parse(resourceJson)?["manifest"]?.GetValue<string>();
            if (manifestStr is null) return (null, $"{kind} manifest missing from response");

            var manifest = JsonNode.Parse(manifestStr);
            string? host = null;

            if (kind == "Ingress")
            {
                // Standard k8s Ingress: spec.rules[0].host
                host = manifest?["spec"]?["rules"]?[0]?["host"]?.GetValue<string>();
            }
            else
            {
                // Traefik IngressRoute: spec.routes[0].match = "Host(`hostname`)"
                var match = manifest?["spec"]?["routes"]?[0]?["match"]?.GetValue<string>();
                if (match is not null)
                {
                    var m = Regex.Match(match, @"Host\(`([^`]+)`\)");
                    if (m.Success) host = m.Groups[1].Value;
                }
            }

            // Detect TLS to decide scheme.
            // Standard Ingress: TLS is declared in spec.tls[].
            // Traefik IngressRoute: HTTPS entrypoint is named "websecure" by convention.
            bool useTls;
            if (kind == "Ingress")
                useTls = manifest?["spec"]?["tls"] is not null;
            else
            {
                var entryPoints = manifest?["spec"]?["entryPoints"]?.AsArray();
                useTls = entryPoints?.Any(ep =>
                    ep?.GetValue<string>()
                      ?.Contains("websecure", StringComparison.OrdinalIgnoreCase) == true) ?? false;
            }

            var scheme = useTls ? "https" : "http";
            return host is not null
                ? ($"{scheme}://{host}/", null)
                : (null, $"{kind} manifest has no resolvable host");
        }
        catch (Exception ex) { return (null, $"Request failed: {ex.Message}"); }
        finally
        {
            owned?.Dispose();
        }
    }

    private static async Task<string?> ReadFromArgoCdCliConfigAsync(string serverHost)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config", "argocd", "config");

        if (!File.Exists(path)) return null;

        try
        {
            var text      = await File.ReadAllTextAsync(path);
            var serverIdx = text.IndexOf(serverHost, StringComparison.Ordinal);
            if (serverIdx < 0) return null;

            // Scope the auth-token search to this server's block only:
            // the block ends at the next "- " entry (next list item in the YAML servers array).
            var nextServerIdx = text.IndexOf("\n- ", serverIdx + 1, StringComparison.Ordinal);
            var searchLength  = nextServerIdx < 0 ? text.Length - serverIdx : nextServerIdx - serverIdx;

            var tokenIdx = text.IndexOf("auth-token:", serverIdx, searchLength, StringComparison.Ordinal);
            if (tokenIdx < 0) return null;

            var start   = tokenIdx + "auth-token:".Length;
            var lineEnd = text.IndexOf('\n', start);
            var token   = (lineEnd < 0 ? text[start..] : text[start..lineEnd]).Trim();

            return string.IsNullOrEmpty(token) ? null : token;
        }
        catch { return null; }
    }
}
