using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inferpal.Config;
using Inferpal.Localization;
using Inferpal.Models;
using Inferpal.Services.Commands;
using Inferpal.Services.Hardware;
using Inferpal.Services.Inference;
using Xunit;

namespace Inferpal.Tests;

// ──────────────────────────────────────────────────────────────────────────────────────────────
//  A backend that does not REPORT per-model VRAM is not a backend whose models hold none.
//
//  LM Studio's native listing says which models are loaded and nothing about what they occupy
//  (size_bytes is the on-disk weight). The client wrote 0 for the missing figure, and every reader
//  took 0 at its word: /hardware answered "Currently loaded: 0 GB · Compute: CPU" — sending a user
//  whose model runs on the GPU to look for a GPU problem — with a headroom equal to the whole
//  budget, and /models printed "0 MB 🟢" beside each loaded model. Ollama's size_vram of 0 does mean
//  CPU, which is why the unknown needs its own value rather than a reinterpretation of 0.
// ──────────────────────────────────────────────────────────────────────────────────────────────
public class UnreportedVramTests
{
    // The measured shape of a live LM Studio /api/v1/models: one loaded model, one idle, and no
    // VRAM figure anywhere in the entry.
    private const string NativePayload = """
        {"models":[
          {"type":"llm","key":"qwen/qwen3.6-27b","size_bytes":17478734335,"max_context_length":262144,
           "loaded_instances":[{"id":"qwen/qwen3.6-27b",
             "config":{"context_length":55296,"parallel":4,"flash_attention":true,"offload_kv_cache_to_gpu":true}}]},
          {"type":"llm","key":"idle-model","size_bytes":4400000000,"max_context_length":32768}
        ]}
        """;

    private static (LoopbackHttpServer Server, LmStudioClient Client, InferpalConfig Config) LmStudio()
    {
        var server = new LoopbackHttpServer(path => path == "/api/v1/models" ? NativePayload : null);
        // A budget is set so the report never probes the machine, and no default model so it asks
        // the backend nothing about architecture: only the running/installed halves are measured.
        var config = new InferpalConfig
        {
            Provider = "lmstudio", BaseUrl = server.BaseUrl, VramBudgetGb = 24, DefaultModel = "",
        };
        return (server, new LmStudioClient(config), config);
    }

    [Fact]
    public async Task TheLmStudioClient_ReportsItsLoadedModel_WithoutAVramFigure()
    {
        var (server, client, _) = LmStudio();
        using (server)
        {
            var loaded = Assert.Single(await client.GetRunningModelsAsync(CancellationToken.None));
            Assert.Equal("qwen/qwen3.6-27b", loaded.Name);
            Assert.False(loaded.ReportsVram);
        }
    }

    [Fact]
    public async Task Hardware_OnLmStudio_NeitherSaysZeroGb_NorCpu_NorAHeadroom()
    {
        var (server, client, config) = LmStudio();
        using (server)
        {
            var report = (await HardwareCommandHandler.HandleAsync(
                config, client, ["/hardware"], CancellationToken.None)).Message;

            // Witnesses: the loaded model reached the report, and the installed table is still there.
            Assert.Contains("| `qwen/qwen3.6-27b` | — |", report);
            Assert.Contains("`idle-model`", report);

            Assert.DoesNotContain(Strings.HardwareCompute("CPU"), report);
            Assert.DoesNotContain(Strings.HardwareCompute("GPU"), report);
            Assert.DoesNotContain(Strings.HardwareLoadedLine("0", Strings.HardwareOfBudget("24"), Strings.HardwareHeadroom("24")), report);
            Assert.DoesNotContain("| 0 GB |", report);
            Assert.Contains(Strings.HardwareLoadedUnreported(1), report);
        }
    }

    [Fact]
    public async Task Models_OnLmStudio_MarksTheLoadedModel_WithoutAZeroMegabyteFigure()
    {
        var (server, client, config) = LmStudio();
        using (server)
        {
            var list = (await ModelsCommandHandler.HandleAsync(
                client, config, ["/models"], CancellationToken.None)).Message;
            Assert.Contains("| `qwen/qwen3.6-27b` | 🟢 |", list);
            Assert.Contains("| `idle-model` | — |", list);
            Assert.DoesNotContain("0 MB", list);

            var running = (await ModelsCommandHandler.HandleAsync(
                client, config, ["/models", "running"], CancellationToken.None)).Message;
            Assert.Contains("| `qwen/qwen3.6-27b` | — |", running);
            Assert.DoesNotContain("0 MB", running);
        }
    }

    // ── The same backend, the same kind of gap: /models delete ─────────────────────────────────
    // LM Studio's API downloads, loads and unloads, and has no delete: every `/models delete`
    // answered "failed to delete", for a model that exists as much as for one that does not.

    [Fact]
    public async Task ModelsDelete_OnLmStudio_SaysTheApiCannotDelete_AndSendsNothing()
    {
        var (server, client, config) = LmStudio();
        using (server)
        {
            var message = (await ModelsCommandHandler.HandleAsync(
                client, config, ["/models", "delete", "qwen/qwen3.6-27b"], CancellationToken.None)).Message;

            Assert.Equal(Strings.ModelsDeleteUnsupported, message);
            Assert.Empty(server.Paths);
        }
    }

    [Fact]
    public async Task ModelsDelete_OnOllama_StillDeletes()
    {
        // Reference arm: the backend that has a delete keeps it.
        var fake = new FakeInferenceProvider { Capabilities = ProviderCapabilities.Ollama };
        await ModelsCommandHandler.HandleAsync(fake, new InferpalConfig(), ["/models", "delete", "llama3.1"], CancellationToken.None);
        Assert.Equal(["llama3.1"], fake.Deleted);
    }

    [Fact]
    public void AReportedZero_StillReadsAsCpu_AndAReportedFigureAsGpu()
    {
        // Reference arm: Ollama's size_vram is a measurement, and 0 there does mean CPU.
        var cpu = new HardwareProfile(24, [new RunningModelInfo("llama3.1", 0, "")], []).FormatReport();
        Assert.Contains(Strings.HardwareCompute("CPU"), cpu);
        Assert.Contains("| `llama3.1` | 0 GB |", cpu);

        var gpu = new HardwareProfile(24, [new RunningModelInfo("llama3.1", 5L * 1024 * 1024 * 1024, "")], []).FormatReport();
        Assert.Contains(Strings.HardwareCompute("GPU"), gpu);
        Assert.Contains(Strings.HardwareHeadroom("19"), gpu);
        Assert.DoesNotContain(Strings.HardwareLoadedUnreported(1), gpu);

        Assert.Contains("| `llama3.1` | 0 MB |", ModelCatalog.FormatRunningModels([new RunningModelInfo("llama3.1", 0, "")]));
    }
}
