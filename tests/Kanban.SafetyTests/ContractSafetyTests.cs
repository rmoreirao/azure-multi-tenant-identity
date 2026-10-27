using System.Text.Json;
using Kanban.Core;
using Kanban.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace Kanban.SafetyTests;

public sealed class ContractSafetyTests
{
    [Fact]
    public async Task PrivateProbeRequiresAnExplicitBoundedCommand()
    {
        Assert.Equal(64, await HostedRuntimeProbe.Main([]));
        Assert.Equal(64, await HostedRuntimeProbe.Main(["--hosted-runtime-probe", "not-a-guid", "candidate"]));
    }

    [Fact]
    public async Task HostedCleanupRejectsAForeignTargetBeforeOpeningSql()
    {
        var path = Path.Combine(AppContext.BaseDirectory, $"invalid-fixture-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """{"server":"unapproved.example.test"}""");
            await Assert.ThrowsAsync<InvalidOperationException>(() => SqlScenario.CleanupHosted(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TextBoundsCountUnicodeScalarsAndUnknownFieldsAreRejected()
    {
        var hundred = string.Concat(Enumerable.Repeat("\U0001F680", 100));
        Assert.Equal(hundred, Rules.Text(hundred, 1, 100));
        Assert.Throws<DomainException>(() => Rules.Text(hundred + "x", 1, 100));
        Assert.Throws<DomainException>(() => Rules.Text("  ", 1, 100));
        Assert.Throws<DomainException>(() => Rules.Key("short"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateCompanyRequest>("""{"Name":"Synthetic","Administrator":true}"""));
    }

    [Fact]
    public void TaskPatchDistinguishesOmittedAssignmentFromExplicitNull()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var omitted = JsonSerializer.Deserialize<UpdateTaskRequest>("""{"title":"Updated"}""", options)!;
        Assert.True(omitted.TitleSpecified);
        Assert.False(omitted.AssigneeSpecified);
        var clear = JsonSerializer.Deserialize<UpdateTaskRequest>("""{"assigneeMembershipId":null}""", options)!;
        Assert.True(clear.AssigneeSpecified);
        Assert.Null(clear.AssigneeMembershipId);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateTaskRequest>("""{"companyId":"override"}""", options));
    }

    [Fact]
    public void CursorProtectionRejectsTamperingAndForeignProtectionKeys()
    {
        var codec = new CursorCodec(new EphemeralDataProtectionProvider());
        var cursor = new CursorState("company:team:caller:query", 50);
        var protectedCursor = codec.Encode(cursor);
        Assert.Equal(cursor, codec.Decode(protectedCursor));
        Assert.Throws<DomainException>(() => codec.Decode("tampered"));
        Assert.Throws<DomainException>(() => new CursorCodec(new EphemeralDataProtectionProvider()).Decode(protectedCursor));
    }
}
