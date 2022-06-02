using System.Globalization;
using k8s;
using k8s.Models;
using Microsoft.Extensions.Options;
using Tyger.Server.Database;
using Tyger.Server.Logging;
using Tyger.Server.Model;
using static Tyger.Server.Kubernetes.KubernetesMetadata;

namespace Tyger.Server.Kubernetes;

public sealed class RunSweeper : IHostedService, IDisposable
{
    private static readonly TimeSpan s_minDurationAfterArchivingBeforeDeletingPod = TimeSpan.FromSeconds(30);

    private Task? _backgroundTask;
    private CancellationTokenSource? _backgroundCancellationTokenSource;
    private readonly IKubernetes _client;
    private readonly IRepository _repository;
    private readonly ILogSource _logSource;
    private readonly ILogArchive _logArchive;
    private readonly KubernetesOptions _k8sOptions;
    private readonly ILogger<RunSweeper> _logger;

    public RunSweeper(
        IKubernetes client,
        IRepository repository,
        IOptions<KubernetesOptions> k8sOptions,
        ILogSource logSource,
        ILogArchive logArchive,
        ILogger<RunSweeper> logger)
    {
        _client = client;
        _repository = repository;
        _logSource = logSource;
        _logArchive = logArchive;
        _k8sOptions = k8sOptions.Value;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _backgroundCancellationTokenSource = new CancellationTokenSource();
        _backgroundTask = BackgroundLoop(_backgroundCancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_backgroundCancellationTokenSource == null || _backgroundTask == null)
        {
            return;
        }

        _backgroundCancellationTokenSource.Cancel();

        // wait for the background task to complete, but give up once the cancellation token is cancelled.
        var tcs = new TaskCompletionSource();
        cancellationToken.Register(s => ((TaskCompletionSource)s!).SetResult(), tcs);
        await Task.WhenAny(_backgroundTask, tcs.Task);
    }

    private async Task BackgroundLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                await SweepRuns(cancellationToken);
            }
            catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _logger.ErrorDuringBackgroundSweep(e);
            }
        }
    }

    public async Task SweepRuns(CancellationToken cancellationToken)
    {
        _logger.StartingBackgroundSweep();

        // first clear out runs that never got a pod created
        while (true)
        {
            var runs = await _repository.GetPageOfRunsThatNeverGotResources(cancellationToken);
            if (runs.Count == 0)
            {
                break;
            }

            foreach (var run in runs)
            {
                _logger.DeletingRunThatNeverCreatedResources(run.Id);
                await DeleteRunResources(run.Id, cancellationToken);
                await _repository.DeleteRun(run.Id, cancellationToken);
            }
        }

        // Now go though the list of jobs on the the cluster
        string? continuation = null;
        do
        {
            var jobs = await _client.ListNamespacedJobAsync(_k8sOptions.Namespace, continueParameter: continuation, labelSelector: JobLabel, cancellationToken: cancellationToken);

            foreach (var job in jobs.Items)
            {
                if (job.Status.Succeeded is > 0 || job.Status.Conditions?.Any(c => c.Type == "Failed" && c.Status == "True") == true)
                {
                    var runId = long.Parse(job.GetLabel(JobLabel), CultureInfo.InvariantCulture);
                    switch (await _repository.GetRun(runId, cancellationToken))
                    {
                        case null:
                            await _repository.DeleteRun(runId, cancellationToken);
                            await DeleteRunResources(runId, cancellationToken);
                            break;
                        case (var run, _, null):
                            await ArchiveLogs(run, cancellationToken);
                            break;
                        case (var run, _, var time) when DateTimeOffset.UtcNow - time > s_minDurationAfterArchivingBeforeDeletingPod:
                            _logger.FinalizingTerminatedRun(run.Id, run.Status);
                            var pods = await _client.EnumeratePodsInNamespace(_k8sOptions.Namespace, labelSelector: $"{JobLabel}={runId}", cancellationToken: cancellationToken)
                                .ToListAsync(cancellationToken);

                            run = RunReader.UpdateRunFromJobAndPods(run, job, pods);
                            await _repository.UpdateRun(run, final: true, cancellationToken: cancellationToken);
                            await DeleteRunResources(run.Id, cancellationToken);
                            break;
                        default:
                            break;
                    }
                }
            }

            continuation = jobs.Metadata.ContinueProperty;
        } while (continuation != null);

        _logger.BackgroundSweepCompleted();
    }

    private async Task ArchiveLogs(Run run, CancellationToken cancellationToken)
    {
        var pipeline = await _logSource.GetLogs(run.Id, new GetLogsOptions { IncludeTimestamps = true }, cancellationToken);
        if (pipeline is null)
        {
            return;
        }

        await _logArchive.ArchiveLogs(run.Id, pipeline, cancellationToken);
        await _repository.UpdateRun(run, logsArchivedAt: DateTimeOffset.UtcNow, cancellationToken: cancellationToken);
    }

    private async Task DeleteRunResources(long runId, CancellationToken cancellationToken)
    {
        string labelSelector = $"{RunLabel}={runId}";
        await foreach (var pod in _client.EnumeratePodsInNamespace(_k8sOptions.Namespace, labelSelector: labelSelector, cancellationToken: cancellationToken))
        {
            // clear finalizer on Pod
            if (pod.RemoveFinalizer(FinalizerName))
            {
                await _client.PatchNamespacedPodAsync(
                    new V1Patch(new { metadata = new { finalizers = pod.Finalizers() } }, V1Patch.PatchType.MergePatch),
                    pod.Name(),
                    pod.Namespace(),
                    cancellationToken: cancellationToken);
            }
        }

        await _client.DeleteCollectionNamespacedJobAsync(_k8sOptions.Namespace, labelSelector: labelSelector, propagationPolicy: "Foreground", cancellationToken: cancellationToken);
        await _client.DeleteCollectionNamespacedStatefulSetAsync(_k8sOptions.Namespace, labelSelector: labelSelector, propagationPolicy: "Foreground", cancellationToken: cancellationToken);
        await _client.DeleteCollectionNamespacedSecretAsync(_k8sOptions.Namespace, labelSelector: labelSelector, propagationPolicy: "Foreground", cancellationToken: cancellationToken);
        await _client.DeleteCollectionNamespacedServiceAsync(_k8sOptions.Namespace, labelSelector: labelSelector, propagationPolicy: "Foreground", cancellationToken: cancellationToken);
    }

    public void Dispose()
    {
        if (_backgroundTask is { IsCompleted: true })
        {
            _backgroundTask.Dispose();
        }
    }
}