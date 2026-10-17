using VideoQuality.Core.Ingest;

namespace VideoQuality.Tests;

public sealed class DropRouterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("vq-drop-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void Auto_without_reference_takes_the_first_file_as_reference()
    {
        string a = Touch("ref.y4m"), b = Touch("a.mp4"), c = Touch("b.mkv");

        DropPlan plan = DropRouter.Plan([a, b, c], DropZone.Auto, null, []);

        Assert.Equal(a, plan.Reference);
        Assert.Equal([b, c], plan.Encodes);
        Assert.False(plan.NeedsConfirm);
        Assert.False(plan.ReferenceOverflowed);
    }

    [Fact]
    public void Auto_with_reference_adds_everything_as_encodes()
    {
        string reference = Touch("ref.mkv"), a = Touch("a.mp4");

        DropPlan plan = DropRouter.Plan([a], DropZone.Auto, reference, []);

        Assert.Null(plan.Reference);
        Assert.Equal([a], plan.Encodes);
    }

    [Fact]
    public void Reference_zone_with_several_files_queues_the_rest_and_offers_undo()
    {
        string a = Touch("ref.mkv"), b = Touch("a.mp4");

        DropPlan plan = DropRouter.Plan([a, b], DropZone.Reference, null, []);

        Assert.Equal(a, plan.Reference);
        Assert.Equal([b], plan.Encodes);
        Assert.True(plan.ReferenceOverflowed);
    }

    [Fact]
    public void Replacing_the_reference_with_encodes_loaded_needs_confirmation()
    {
        string old = Touch("old.mkv"), replacement = Touch("new.mkv"), encode = Touch("a.mp4");

        DropPlan plan = DropRouter.Plan([replacement], DropZone.Reference, old, [encode]);

        Assert.Equal(replacement, plan.Reference);
        Assert.True(plan.NeedsConfirm);
    }

    [Fact]
    public void Replacing_the_reference_without_encodes_needs_no_confirmation()
    {
        string old = Touch("old.mkv"), replacement = Touch("new.mkv");

        Assert.False(DropRouter.Plan([replacement], DropZone.Reference, old, []).NeedsConfirm);
    }

    [Fact]
    public void Promoting_the_only_encode_to_reference_needs_no_confirmation()
    {
        string old = Touch("old.mkv"), encode = Touch("a.mp4");

        DropPlan plan = DropRouter.Plan([encode], DropZone.Reference, old, [encode]);

        Assert.Equal(encode, plan.Reference);
        Assert.False(plan.NeedsConfirm);
    }

    [Fact]
    public void Encodes_zone_never_sets_a_reference()
    {
        string a = Touch("a.mp4"), b = Touch("b.mp4");

        DropPlan plan = DropRouter.Plan([a, b], DropZone.Encodes, null, []);

        Assert.Null(plan.Reference);
        Assert.Equal([a, b], plan.Encodes);
    }

    [Fact]
    public void Folders_are_scanned_for_video_files_only()
    {
        Touch("clips/b.webm");
        Touch("clips/a.ivf");
        Touch("clips/notes.txt");
        Touch("clips/nested/deep.mp4");

        DropPlan plan = DropRouter.Plan([Path.Combine(_root, "clips")], DropZone.Encodes, null, []);

        Assert.Equal(["a.ivf", "b.webm"], plan.Encodes.Select(Path.GetFileName));
        Assert.Empty(plan.Rejected);
    }

    [Fact]
    public void Non_video_missing_and_non_local_paths_are_rejected_with_reasons()
    {
        string text = Touch("readme.txt");
        string missing = Path.Combine(_root, "gone.mp4");

        DropPlan plan = DropRouter.Plan([text, missing, null], DropZone.Auto, null, []);

        Assert.True(plan.IsEmpty);
        Assert.Collection(
            plan.Rejected,
            r => Assert.Equal("not a video file (.txt)", r.Reason),
            r => Assert.Equal("file not found", r.Reason),
            r => Assert.Equal(DropRouter.NonLocalReason, r.Reason));
    }

    [Fact]
    public void Duplicates_of_loaded_files_and_within_the_drop_are_ignored()
    {
        string reference = Touch("ref.mkv"), loaded = Touch("a.mp4"), fresh = Touch("b.mp4");

        DropPlan plan = DropRouter.Plan([loaded, fresh, fresh, reference], DropZone.Encodes, reference, [loaded]);

        Assert.Equal([fresh], plan.Encodes);
        Assert.Equal(3, plan.Duplicates.Count);
    }

    [Fact]
    public void Re_dropping_the_current_reference_is_a_no_op()
    {
        string reference = Touch("ref.mkv"), encode = Touch("a.mp4");

        DropPlan plan = DropRouter.Plan([reference], DropZone.Reference, reference, [encode]);

        Assert.True(plan.IsEmpty);
        Assert.False(plan.NeedsConfirm);
        Assert.Equal([reference], plan.Duplicates);
    }
}
