using ClaudeWidget.Core.Agents;

namespace ClaudeWidget.Tests.Agents;

/// <summary>Lenient JSON mapping of `claude agents --json --all` entries — one bad element must not kill the listing.</summary>
public sealed class AgentEntryTests
{
    [Fact]
    public void A_null_element_is_skipped_and_the_rest_of_the_listing_survives()
    {
        var listing = """[null, {"sessionId":"a","kind":"interactive","pid":1}]""";
        var entries = AgentEntry.ParseListing(listing);
        Assert.Single(entries);
        Assert.Equal("a", entries[0].SessionId);
    }

    [Fact]
    public void A_non_object_element_is_skipped()
    {
        var listing = """["just a string", 42, {"sessionId":"a"}]""";
        var entries = AgentEntry.ParseListing(listing);
        Assert.Single(entries);
        Assert.Equal("a", entries[0].SessionId);
    }

    [Fact]
    public void An_entry_without_a_string_sessionId_is_skipped()
    {
        var listing = """[{"sessionId":42}, {"sessionId":"a"}]""";
        var entries = AgentEntry.ParseListing(listing);
        Assert.Single(entries);
        Assert.Equal("a", entries[0].SessionId);
    }

    [Fact]
    public void A_fractional_pid_maps_to_null_not_an_error()
    {
        var listing = """[{"sessionId":"a","pid":42.5}]""";
        var entries = AgentEntry.ParseListing(listing);
        Assert.Null(entries[0].Pid);
    }

    [Fact]
    public void A_string_pid_maps_to_null_not_an_error()
    {
        var listing = """[{"sessionId":"a","pid":"42"}]""";
        var entries = AgentEntry.ParseListing(listing);
        Assert.Null(entries[0].Pid);
    }

    [Fact]
    public void An_integer_pid_is_kept()
    {
        var listing = """[{"sessionId":"a","pid":42}]""";
        var entries = AgentEntry.ParseListing(listing);
        Assert.Equal(42, entries[0].Pid);
    }

    [Fact]
    public void A_non_string_name_becomes_empty_not_an_error()
    {
        var listing = """[{"sessionId":"a","name":123}]""";
        var entries = AgentEntry.ParseListing(listing);
        Assert.Equal("", entries[0].Name);
    }

    [Fact]
    public void A_non_finite_or_missing_startedAt_maps_to_null()
    {
        var listing = """[{"sessionId":"a"}, {"sessionId":"b","startedAt":"soon"}]""";
        var entries = AgentEntry.ParseListing(listing);
        Assert.All(entries, e => Assert.Null(e.StartedAt));
    }

    [Fact]
    public void A_numeric_state_is_stringified_like_JS_String_coercion()
    {
        var listing = """[{"sessionId":"a","state":7}]""";
        var entries = AgentEntry.ParseListing(listing);
        Assert.Equal("7", entries[0].State);
    }

    [Fact]
    public void Root_that_is_not_an_array_throws()
    {
        Assert.Throws<InvalidOperationException>(() => AgentEntry.ParseListing("""{"not":"an array"}"""));
    }
}
