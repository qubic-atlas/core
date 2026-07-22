using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace QubicAtlas;

// Shells out to the native C++ verifier binary:
//   verifier <miningSeedHex> <pubKeyHex> <nonceHex> <expectedScore|-1>
// Prints reconstruction JSON to stdout. Port of index.js execFileP usage.
public sealed class Verifier
{
    public string Path { get; }
    public Verifier(string path) => Path = path;

    public bool Exists => File.Exists(Path);
    public static bool ExistsAt(string path) => File.Exists(path);

    // Run the default (build3) binary — kept for callers that don't dispatch by epoch.
    public Task<JsonNode> RunAsync(string miningSeed, string pubKey, string nonce,
        string expectedScore = "-1", int timeoutMs = 60000)
        => RunAtAsync(Path, miningSeed, pubKey, nonce, expectedScore, timeoutMs);

    // Run a SPECIFIC binary (epoch-dispatched: build0/1/2/build3). CLI is identical for all.
    public async Task<JsonNode> RunAtAsync(string binaryPath, string miningSeed, string pubKey,
        string nonce, string expectedScore = "-1", int timeoutMs = 60000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(miningSeed);
        psi.ArgumentList.Add(pubKey);
        psi.ArgumentList.Add(nonce);
        psi.ArgumentList.Add(expectedScore);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        if (!proc.Start())
            throw new Exception("failed to start verifier");
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch { }
            throw new Exception("verifier timeout");
        }

        if (proc.ExitCode != 0)
            throw new Exception(stderr.Length > 0 ? stderr.ToString().Trim() : $"verifier exit {proc.ExitCode}");

        var text = stdout.ToString().Trim();
        var node = JsonNode.Parse(text);
        if (node is null)
            throw new Exception("verifier produced no JSON: " + (stderr.Length > 0 ? stderr.ToString().Trim() : text));
        return node;
    }
}
