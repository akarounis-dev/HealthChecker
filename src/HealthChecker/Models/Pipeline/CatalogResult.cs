namespace HealthChecker.Models;

record CatalogResult(
    string   ResolvedVersion,
    string[] VmTargets,               // numeric-suffix targets (VM instances)
    string?  KubernetesCluster = null // non-numeric target (k8s cluster name); null if pure VM
);
