// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Text.Json;
using XboxDevCLI.Services;
using Xunit;

namespace XboxDevCLI.UnitTest;

public class XboxPartnerGameCreationJobTests
{
    [Theory]
    [InlineData("notStarted", "pending", false, false)]
    [InlineData("running", "pending", false, false)]
    [InlineData("futureStatus", "succeeded", false, false)]
    [InlineData(null, null, false, false)]
    [InlineData("completed", "succeeded", true, true)]
    [InlineData("completed", "failed", true, false)]
    [InlineData("completed", "pending", true, false)]
    [InlineData("completed", null, true, false)]
    [InlineData("completed", "futureResult", true, false)]
    public void Completion_DependsOnStatusAndResult(
        string? status, string? result, bool completed, bool succeeded)
    {
        var job = new XboxPartnerGameCreationJob { JobId = "job-1", Status = status, Result = result };

        Assert.Equal(completed, job.Completed);
        Assert.Equal(succeeded, job.Succeeded);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"jobId":42}""")]
    [InlineData("""{"jobId":"job-1","status":false}""")]
    [InlineData("""{"jobId":"job-1","result":42}""")]
    public void Deserialize_RejectsInvalidFieldTypes(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<XboxPartnerGameCreationJob>(json));

    [Fact]
    public void Serialize_ExcludesTransportMetadata()
    {
        var job = new XboxPartnerGameCreationJob
        {
            JobId = "job-1",
            RawResponse = "original body",
            RetryAfter = TimeSpan.FromSeconds(9),
            CorrelationId = "correlation-1",
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(job));

        Assert.Equal("job-1", json.RootElement.GetProperty("jobId").GetString());
        Assert.Equal(3, json.RootElement.EnumerateObject().Count());
    }
}
