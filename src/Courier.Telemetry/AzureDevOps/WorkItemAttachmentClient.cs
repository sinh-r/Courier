using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Courier.Core.Abstractions;
using Courier.Core.Privacy;

namespace Courier.Telemetry.AzureDevOps;

/// <summary>
/// Attaches a capsule to an Azure DevOps work item, and opens one from an attachment. CAP-08.
/// </summary>
/// <remarks>
/// <para>
/// This is the last step of the Phase 4 definition of done: "a tester who has never opened the app
/// can produce a capsule from a failing call and attach it to a bug, and the developer opens it and
/// reproduces in one click."
/// </para>
/// <para>
/// The personal access token comes from the credential store, and the organisation host is
/// registered with the egress policy only when the user configures it — a work item tracker is not
/// a destination Courier contacts otherwise.
/// </para>
/// <para>
/// <b>Needs live validation.</b> Written against the documented REST API version 7.1; not
/// exercised against a real organisation from this machine.
/// </para>
/// </remarks>
public sealed class WorkItemAttachmentClient
{
    private const string Initiator = nameof(WorkItemAttachmentClient);
    private const string ApiVersion = "7.1";

    private readonly EgressGate _gate;
    private readonly ISecretStore _secrets;

    public WorkItemAttachmentClient(
        string organisation,
        string project,
        EgressGate gate,
        UserIntentEgressPolicy policy,
        ISecretStore secrets)
    {
        Organisation = organisation;
        Project = project;
        _gate = gate;
        _secrets = secrets;

        BaseAddress = new Uri($"https://dev.azure.com/{organisation}/");
        policy.Register(BaseAddress, EgressPurpose.WorkItemTracker);
    }

    public string Organisation { get; }

    public string Project { get; }

    public Uri BaseAddress { get; }

    public SecretKey TokenKey => new(SecretKey.AuthScope, $"azuredevops/{Organisation}");

    /// <summary>
    /// Uploads a capsule and links it to the work item. Returns the attachment URL so the caller
    /// can show what it created rather than reporting a bare success.
    /// </summary>
    public async Task<Uri> AttachAsync(
        int workItemId,
        string fileName,
        Stream capsule,
        string? comment = null,
        CancellationToken ct = default)
    {
        using var client = await CreateClientAsync(ct).ConfigureAwait(false);

        // Two steps, as the API requires: upload to get an id, then patch the work item to link it.
        using var content = new StreamContent(capsule);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var upload = await client
            .PostAsync(
                $"{Project}/_apis/wit/attachments?fileName={Uri.EscapeDataString(fileName)}&api-version={ApiVersion}",
                content,
                ct)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(upload, "upload the capsule", ct).ConfigureAwait(false);

        using var uploadDocument = await JsonDocument
            .ParseAsync(await upload.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        var attachmentUrl = uploadDocument.RootElement.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Azure DevOps accepted the upload but returned no URL.");

        var patch = new[]
        {
            new
            {
                op = "add",
                path = "/relations/-",
                value = new
                {
                    rel = "AttachedFile",
                    url = attachmentUrl,
                    attributes = new { comment = comment ?? "Courier capsule" },
                },
            },
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{Project}/_apis/wit/workitems/{workItemId}?api-version={ApiVersion}")
        {
            Content = JsonContent.Create(patch, mediaType: new MediaTypeHeaderValue("application/json-patch+json")),
        };

        using var link = await client.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(link, $"attach the capsule to work item {workItemId}", ct).ConfigureAwait(false);

        return new Uri(attachmentUrl);
    }

    /// <summary>
    /// Downloads a capsule attachment. CAP-05: opening a capsule from a work item is one of the
    /// four import paths.
    /// </summary>
    public async Task<Stream> DownloadAsync(Uri attachmentUrl, CancellationToken ct = default)
    {
        using var client = await CreateClientAsync(ct).ConfigureAwait(false);

        var response = await client.GetAsync(attachmentUrl, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, "download the capsule", ct).ConfigureAwait(false);

        // Copied to memory so the response can be disposed; a capsule is small by construction.
        var buffer = new MemoryStream();
        await response.Content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;
        return buffer;
    }

    /// <summary>Lists the capsule attachments on a work item, for the open-from-work-item flow.</summary>
    public async Task<IReadOnlyList<CapsuleAttachment>> ListCapsulesAsync(
        int workItemId,
        CancellationToken ct = default)
    {
        using var client = await CreateClientAsync(ct).ConfigureAwait(false);

        using var response = await client
            .GetAsync($"{Project}/_apis/wit/workitems/{workItemId}?$expand=relations&api-version={ApiVersion}", ct)
            .ConfigureAwait(false);

        await EnsureSuccessAsync(response, $"read work item {workItemId}", ct).ConfigureAwait(false);

        using var document = await JsonDocument
            .ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct)
            .ConfigureAwait(false);

        var attachments = new List<CapsuleAttachment>();

        if (!document.RootElement.TryGetProperty("relations", out var relations))
        {
            return attachments;
        }

        foreach (var relation in relations.EnumerateArray())
        {
            if (relation.TryGetProperty("rel", out var rel)
                && rel.GetString() == "AttachedFile"
                && relation.TryGetProperty("url", out var url))
            {
                var name = relation.TryGetProperty("attributes", out var attributes)
                    && attributes.TryGetProperty("name", out var nameElement)
                        ? nameElement.GetString() ?? "attachment"
                        : "attachment";

                if (name.EndsWith(Core.Capsules.CapsuleFormat.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    attachments.Add(new CapsuleAttachment(name, new Uri(url.GetString()!)));
                }
            }
        }

        return attachments;
    }

    private async Task<HttpClient> CreateClientAsync(CancellationToken ct)
    {
        var token = await _secrets.GetAsync(TokenKey, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No Azure DevOps token is stored for {Organisation}. Add a personal access token "
                + "with work item read and write scope; it goes to the credential store.");

        var client = _gate.CreateClient(EgressPurpose.WorkItemTracker, Initiator);
        client.BaseAddress = BaseAddress;

        // Azure DevOps takes a PAT as the password of a basic credential with an empty username.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token}")));

        return client;
    }

    /// <summary>
    /// Turns a failure into a sentence that says what to do. UI_SPEC 3.7 applies to integrations
    /// as much as to the request pane, and "403" on its own sends people to the wrong place.
    /// </summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var message = (int)response.StatusCode switch
        {
            401 => "Azure DevOps rejected the token. Check it has not expired and that it covers work items.",
            403 => "The token is valid but lacks permission for work items. It needs read and write on Work Items.",
            404 => "Azure DevOps could not find that work item or project. Check the organisation and project names.",
            _ => $"Azure DevOps returned {(int)response.StatusCode} {response.ReasonPhrase}.",
        };

        throw new InvalidOperationException(
            $"Could not {what}. {message}"
            + (detail.Length is > 0 and < 400 ? $" ({detail.Trim()})" : string.Empty));
    }
}

public sealed record CapsuleAttachment(string FileName, Uri Url);
