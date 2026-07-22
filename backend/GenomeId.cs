using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QubicAtlas;

// Computes a content hash (annGenomeId) of the trained network the worker submitted.
//
// The verifier is DETERMINISTIC: identical public inputs + the canonical Core scorer
// produce byte-identical output. So there is exactly ONE correct (score, genome) per proof.
// We derive genome_id SERVER-SIDE from the submitted reconstruction — never trust a
// worker-supplied id — over a canonical serialization of only the fields that define the
// network: { algorithm, score, metrics, graph.nodes, graph.links, graph.matrix }, with
// object keys sorted recursively and numbers preserved verbatim. Honest workers running the
// same binary therefore hash to the same genome_id; a divergent id means modified/buggy code.
public static class GenomeId
{
    public static string Compute(JsonNode? reconstruction)
    {
        if (reconstruction is null) return "";
        var recon = reconstruction.AsObject();
        var graph = recon["graph"]?.AsObject();

        var canonical = new JsonObject
        {
            ["algorithm"] = recon["algorithm"]?.DeepClone(),
            ["score"] = recon["score"]?.DeepClone(),
            ["metrics"] = recon["metrics"]?.DeepClone(),
            ["graph"] = new JsonObject
            {
                ["nodes"] = graph?["nodes"]?.DeepClone(),
                ["links"] = graph?["links"]?.DeepClone(),
                ["matrix"] = graph?["matrix"]?.DeepClone(),
            },
        };

        var sb = new StringBuilder(1 << 20);
        Write(canonical, sb);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // Deterministic serializer: object keys emitted in sorted order; arrays in document
    // order; scalars via JsonElement.GetRawText() so number/string formatting is byte-stable.
    private static void Write(JsonNode? node, StringBuilder sb)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                bool firstK = true;
                foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!firstK) sb.Append(',');
                    firstK = false;
                    sb.Append(JsonSerializer.Serialize(kv.Key));
                    sb.Append(':');
                    Write(kv.Value, sb);
                }
                sb.Append('}');
                break;
            case JsonArray arr:
                sb.Append('[');
                bool firstV = true;
                foreach (var v in arr)
                {
                    if (!firstV) sb.Append(',');
                    firstV = false;
                    Write(v, sb);
                }
                sb.Append(']');
                break;
            default: // JsonValue
                sb.Append(node.GetValue<JsonElement>().GetRawText());
                break;
        }
    }
}
