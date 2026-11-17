using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static TigerCS.Tests.QueueingIntegration.QueueingFixture;

namespace TigerCS.Tests.QueueingIntegration;

/// <summary>
/// The documented request / response JSON is not prose: every sample under <c>docs/QueueingIntegration/samples</c> is posted to the real API in order, and the
/// response must equal the sample exactly (only the ticket id / number, which depend on the database, are normalised). If the contract drifts, the documentation
/// - and the sender that is built from it - fails here. The same files are checked from the sending side by the Queueing sender's own tests.
/// </summary>
public sealed class QueueingContractSampleTests(QueueingFixture f) : IClassFixture<QueueingFixture>
{
    private static string SamplesDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "QueueingIntegration", "samples");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("docs/QueueingIntegration/samples was not found above the test output directory.");
    }

    private static JsonNode Sample(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(SamplesDirectory(), name)))!;

    private async Task<(HttpStatusCode Status, JsonNode Body)> PostSampleAsync(JsonNode request)
    {
        var http = await f.CreateClient().PostAsync("/api/integrations/queueing/events",
            new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"));
        return (http.StatusCode, JsonNode.Parse(await http.Content.ReadAsStringAsync())!);
    }

    private static void AssertMatchesSample(string name, JsonNode actual)
    {
        var expected = Sample(name);
        // The ticket id / number depend on the database; everything else must be identical, including member order and absent members.
        foreach (var normalised in new[] { "ticketId", "ticketNumber" })
        {
            if (actual[normalised] is not null && expected[normalised] is not null)
            {
                actual[normalised] = expected[normalised]!.DeepClone();
            }
        }

        Assert.True(JsonNode.DeepEquals(expected, actual), $"{name}\nexpected: {expected.ToJsonString()}\nactual:   {actual.ToJsonString()}");
        Assert.Equal(
            expected.AsObject().Select(p => p.Key),
            actual.AsObject().Select(p => p.Key));
    }

    private async Task AssertSampleAsync(string baseName, HttpStatusCode expectedStatus, JsonNode? request = null)
    {
        var (status, body) = await PostSampleAsync(request ?? Sample(baseName + ".request.json"));
        Assert.Equal(expectedStatus, status);
        AssertMatchesSample(baseName + ".response.json", body);
    }

    [Fact]
    public async Task EverySampleInTheContract_IsExactlyWhatTheApiAnswers()
    {
        // One queue ticket's whole life, in order.
        await AssertSampleAsync("01-TicketCreated", HttpStatusCode.Created);
        await AssertSampleAsync("02-TicketAssigned", HttpStatusCode.OK);
        await AssertSampleAsync("03-TicketCommentAdded", HttpStatusCode.OK);
        await AssertSampleAsync("04-TicketRecalled", HttpStatusCode.OK);
        await AssertSampleAsync("05-TicketReturned", HttpStatusCode.OK);
        await AssertSampleAsync("06-TicketReassigned", HttpStatusCode.OK);
        await AssertSampleAsync("07-TicketClosed", HttpStatusCode.OK);

        // The same close again: a Duplicate, answered with the original outcome.
        await AssertSampleAsync("09-Duplicate", HttpStatusCode.OK, Sample("07-TicketClosed.request.json"));

        // A stale event (sequence 3 after sequence 7): superseded, recorded, never applied.
        await AssertSampleAsync("11-StaleEvent", HttpStatusCode.OK);

        // The same EventId with different content.
        await AssertSampleAsync("13-EventIdReused", HttpStatusCode.Conflict);

        // A second queue ticket that is cancelled: created exactly like the first (new EventId / ExternalTicketId), then the sample cancel.
        var second = Sample("01-TicketCreated.request.json");
        second["EventId"] = "0f6c1d52-3b7a-4a55-9c53-0d6a1f1b0010";
        second["ExternalTicketId"] = "QT-20261110-0002";
        second["QueueNumber"] = "A-018";
        Assert.Equal(HttpStatusCode.Created, (await PostSampleAsync(second)).Status);
        await AssertSampleAsync("08-TicketCancelled", HttpStatusCode.OK);

        // An event that arrives before its ticket, and an invalid one.
        await AssertSampleAsync("10-AwaitingTicket", HttpStatusCode.Accepted);
        await AssertSampleAsync("12-Invalid", HttpStatusCode.BadRequest);
    }

    [Fact]
    public void EverySampleRequest_IsValidJson_WithPascalCaseMembers_AndNamedEventTypes()
    {
        foreach (var file in Directory.GetFiles(SamplesDirectory(), "*.request.json"))
        {
            var node = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Assert.All(node.Select(p => p.Key), key => Assert.True(char.IsUpper(key[0]), $"{Path.GetFileName(file)}: {key}"));
            Assert.Equal(JsonValueKind.String, node["EventType"]!.GetValueKind());
            Assert.Equal(JsonValueKind.Number, node["EventSequence"]!.GetValueKind());
        }
    }
}
