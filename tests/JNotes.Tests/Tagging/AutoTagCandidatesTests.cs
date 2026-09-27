using JNotes.Core.Tagging;

namespace JNotes.Tests.Tagging;

public class AutoTagCandidatesTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid[] ANotes = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    private static readonly Guid[] BNotes = [Guid.NewGuid()];

    [Fact]
    public void Interleaves_round_robin_and_keeps_each_users_order()
    {
        var result = AutoTagCandidates.Interleave([(A, ANotes), (B, BNotes)], 10);

        Assert.Equal([(A, ANotes[0]), (B, BNotes[0]), (A, ANotes[1]), (A, ANotes[2])], result);
    }

    [Fact]
    public void Stops_at_max()
    {
        var result = AutoTagCandidates.Interleave([(A, ANotes), (B, BNotes)], 2);

        Assert.Equal([(A, ANotes[0]), (B, BNotes[0])], result);
    }

    [Fact]
    public void Nothing_in_gives_nothing_out()
    {
        Assert.Empty(AutoTagCandidates.Interleave([], 10));
        Assert.Empty(AutoTagCandidates.Interleave([(A, ANotes)], 0));
    }

    [Fact]
    public void A_user_with_no_notes_is_skipped()
    {
        var result = AutoTagCandidates.Interleave([(A, []), (B, BNotes)], 10);

        Assert.Equal([(B, BNotes[0])], result);
    }
}
