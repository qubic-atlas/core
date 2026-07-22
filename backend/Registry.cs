namespace QubicAtlas;

// Epoch -> Core version -> ANN parameter set registry.
// Port of server/registry.js. Source of truth: qubic/core public_settings.h at each
// release tag. The verifier binary is compiled per parameter set; here we record which
// epochs a build covers. MVP scope: recent epochs on dual_hyperidentity_addition (v1.29x).
public sealed record AlgoParams(
    int InputNeurons, int OutputNeurons, int Ticks, int Neighbors,
    int Population, int Mutations, int Threshold);

public sealed record ParamSet(
    int Epoch, string CoreVersion, string AlgoFamily,
    AlgoParams HyperIdentity, AlgoParams Addition);

public static class Registry
{
    public static readonly IReadOnlyDictionary<string, ParamSet> ParamSets =
        new Dictionary<string, ParamSet>
        {
            ["epoch-222-v1.299.1"] = new ParamSet(
                Epoch: 222, CoreVersion: "v1.299.1", AlgoFamily: "dual_hyperidentity_addition",
                HyperIdentity: new AlgoParams(512, 512, 1000, 728, 1174, 150, 316),
                Addition: new AlgoParams(14, 8, 256, 256, 256, 256, 74100))
        };

    // The active param set the currently-built verifier binary reproduces.
    public static readonly ParamSet P = ParamSets["epoch-222-v1.299.1"];

    // Per-algorithm threshold lookup.
    public static readonly IReadOnlyDictionary<string, int> Threshold =
        new Dictionary<string, int>
        {
            ["HyperIdentity"] = P.HyperIdentity.Threshold,
            ["Addition"] = P.Addition.Threshold
        };

    // Epochs the currently-built verifier binary reproduces exactly (same param set as HEAD).
    // v1.297-v1.299 share the epoch-222 parameter set on this family.
    public static readonly int[] SupportedEpochs = { 220, 221, 222, 223 };

    public static (bool Supported, string ParamSetId) ParamSetForEpoch(int? epoch)
    {
        bool supported = epoch.HasValue && IsSupported(epoch.Value);
        return (supported, "epoch-222-v1.299.1");
    }

    // ======================= Multi-epoch (backward-compatible) verification =======================
    // The verifier image ships FOUR era-specific binaries, each reproducing its epochs byte-exact:
    //   build3 (current, /usr/local/bin/verifier)  -> epochs 215–223
    //   build0 (verifier-build0)                    -> epochs 197–203
    //   build1 (verifier-build1)                    -> epochs 204–206
    //   build2 (verifier-build2)                    -> epochs 207–214
    // Epochs <= 196 use a DIFFERENT mining algorithm and are UNSUPPORTED (never enqueue/verify).
    //
    // The historical binaries carry a build-wide threshold that is imprecise across the version
    // span they cover; the table below records the exact per-epoch thresholds (HyperIdentity /
    // Addition) so the server can override passesThreshold authoritatively (pass == score >= threshold).
    public sealed record EpochBuild(string Build, int HyperIdentity, int Addition);

    // The reconstructorVersion the historical (build0/1/2) binaries emit — trusted alongside the
    // current build's ATLAS_VERIFIER_VERSION for consensus pinning.
    public const string HistoricalVerifierVersion = "qubic-atlas-hist-1";
    public const int MinSupportedEpoch = 197;

    public static readonly IReadOnlyDictionary<int, EpochBuild> EpochBuilds =
        new Dictionary<int, EpochBuild>
        {
            [197] = new("build0", 321, 74200), [198] = new("build0", 321, 74200),
            [199] = new("build0", 321, 74194), [200] = new("build0", 321, 74196),
            [201] = new("build0", 321, 74300), [202] = new("build0", 321, 74300),
            [203] = new("build0", 321, 74500),
            [204] = new("build1", 321, 74500), [205] = new("build1", 321, 74800),
            [206] = new("build1", 321, 75200),
            [207] = new("build2", 321, 75200), [208] = new("build2", 321, 75700),
            [209] = new("build2", 321, 75700), [210] = new("build2", 321, 76100),
            [211] = new("build2", 321, 76100), [212] = new("build2", 321, 76100),
            [213] = new("build2", 321, 76200), [214] = new("build2", 321, 76430),
            [215] = new("build3", 321, 76500), [216] = new("build3", 321, 76000),
            [217] = new("build3", 316, 74300), [218] = new("build3", 316, 74100),
            [219] = new("build3", 316, 74100), [220] = new("build3", 316, 74100),
            [221] = new("build3", 316, 74100), [222] = new("build3", 316, 74100),
            [223] = new("build3", 316, 74100),
        };

    // Core release span each build was compiled from (dual_hyperidentity_addition family).
    // A build covers several releases, so this is the version RANGE — not one exact version.
    public static readonly IReadOnlyDictionary<string, string> BuildCoreVersion =
        new Dictionary<string, string>
        {
            ["build0"] = "v1.275–v1.281",
            ["build1"] = "v1.282–v1.284",
            ["build2"] = "v1.285–v1.292",
            ["build3"] = "v1.293–v1.299",
        };

    // The Core version (range) for an epoch, via its build. "—" if unsupported.
    public static string CoreVersionForEpoch(int epoch)
        => EpochBuilds.TryGetValue(epoch, out var e) && BuildCoreVersion.TryGetValue(e.Build, out var v) ? v : "—";

    // Highest epoch a bundled binary reproduces (grows as new epochs are added above).
    public static readonly int MaxSupportedEpoch = EpochBuilds.Keys.Max();

    // Is this epoch reproducible by one of the bundled binaries? (false for <=196 and anything unlisted)
    public static bool IsSupported(int epoch) => EpochBuilds.ContainsKey(epoch);
    public static bool IsSupported(int? epoch) => epoch.HasValue && EpochBuilds.ContainsKey(epoch.Value);

    // "build0".."build3" for a supported epoch, else null.
    public static string? BuildForEpoch(int epoch)
        => EpochBuilds.TryGetValue(epoch, out var e) ? e.Build : null;

    // Exact per-epoch (HyperIdentity, Addition) thresholds. Falls back to the active param set
    // for unsupported epochs (callers should guard on IsSupported before verifying).
    public static (int Hi, int Add) ThresholdsForEpoch(int epoch)
        => EpochBuilds.TryGetValue(epoch, out var e)
            ? (e.HyperIdentity, e.Addition)
            : (P.HyperIdentity.Threshold, P.Addition.Threshold);

    // Per-epoch threshold for a specific algorithm (pass rule is score >= threshold for both).
    public static int ThresholdForEpoch(int epoch, string algorithm)
    {
        var (hi, add) = ThresholdsForEpoch(epoch);
        return algorithm == "Addition" ? add : hi;
    }

    // Resolve the binary to run for an epoch. build3 => the current build3Path (the VERIFIER env);
    // build0/1/2 => sibling "verifier-<build>" in the SAME directory as build3Path.
    public static string VerifierPathForEpoch(int epoch, string build3Path)
    {
        var build = BuildForEpoch(epoch);
        if (build is null || build == "build3") return build3Path;
        var dir = System.IO.Path.GetDirectoryName(build3Path);
        return string.IsNullOrEmpty(dir)
            ? $"verifier-{build}"
            : System.IO.Path.Combine(dir, $"verifier-{build}");
    }
}
