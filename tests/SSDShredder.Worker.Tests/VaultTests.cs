using SSDShredder.Worker.Protocol;
using SSDShredder.Worker.Simulation;
using SSDShredder.Worker.Vault;

namespace SSDShredder.Worker.Tests;

public sealed class VaultFixture : IDisposable
{
    public const string Owner = "S-1-5-21-1000-2000-3000-1001";
    public const string Stranger = "S-1-5-21-1000-2000-3000-1002";

    private readonly TempDir _dir = new();

    public VaultFixture()
    {
        Journal = new VaultJournal(Path.Combine(_dir.Path, "journal"));
    }

    public SimulatedVaultPlatform Platform { get; } = new();
    public VaultJournal Journal { get; }
    public string JournalDir => Journal.Directory;

    /// <summary>A new service instance stands for a new worker process.</summary>
    public VaultService NewService(bool optIn = true, bool detachRoute = false) => new(
        Journal,
        Platform,
        new VaultOptions
        {
            ImageDirectory = Path.Combine(_dir.Path, "images"),
            MountRoot = Path.Combine(_dir.Path, "mounts"),
            OrphanRetirementOptIn = optIn,
            AllowDetachCloseRoute = detachRoute,
            EncryptPoll = TimeSpan.FromMilliseconds(1),
            Sleep = _ => { },
            LockWait = TimeSpan.FromSeconds(10),
        });

    public (VaultService service, Guid id, VaultRecord record) Ready(bool optIn = true, bool detachRoute = false)
    {
        VaultService service = NewService(optIn, detachRoute);
        VaultOutcome o = service.Init(Owner);
        Assert.Equal(ExitCode.Completed, o.Code);
        Guid id = Guid.Parse(o.Evidence.VaultId!);
        return (service, id, Journal.Load(id)!);
    }

    public VaultRecord Only() => Journal.Load(Assert.Single(Journal.ListIds()))!;

    public void Dispose() => _dir.Dispose();
}

public class VaultInitTests
{
    [Fact]
    public void A_vault_is_published_only_after_verified_readiness()
    {
        using var f = new VaultFixture();
        var phases = new List<string>();
        VaultOutcome o = f.NewService().Init(VaultFixture.Owner, phases.Add);

        Assert.Equal(ExitCode.Completed, o.Code);
        Assert.Equal(VaultPhases.Ready, o.Evidence.Phase);
        Assert.NotNull(o.Evidence.Mount);
        Assert.Equal("fullyEncrypted", o.Evidence.Ready!.Conversion);
        Assert.True(o.Evidence.Ready.ProtectionOn);
        Assert.Equal([8], o.Evidence.Ready.ProtectorTypes);
        Assert.False(o.Evidence.Ready.AutoUnlock);
        Assert.Equal(7, o.Evidence.Ready.EncryptionMethod);
        Assert.Equal(["creating", "encrypting"], phases);

        // The mount comes after encryption was started and its state was read.
        List<string> calls = f.Platform.Calls;
        Assert.True(calls.IndexOf("mount") > calls.LastIndexOf("query"));
        Assert.True(calls.IndexOf("format") > calls.IndexOf("attach"));

        VaultRecord rec = f.Only();
        Assert.Equal(VaultPhases.Ready, rec.Phase);
        Assert.Equal(VaultFixture.Owner, rec.OwnerSid);
        Assert.NotNull(rec.BackingFileId);
        Assert.NotNull(rec.VhdId);
        Assert.NotNull(rec.GptDiskId);
        Assert.NotNull(rec.PartitionId);
        Assert.NotNull(rec.VolumeGuid);
    }

    [Fact]
    public void The_session_passphrase_is_random_and_never_written_to_the_journal()
    {
        using var f = new VaultFixture();
        f.Ready();
        string first = f.Platform.LastPassphraseForTests!;
        Assert.Equal(64, first.Length);
        Assert.All(first, c => Assert.True(Uri.IsHexDigit(c)));

        foreach (string file in Directory.EnumerateFiles(f.JournalDir, "*", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            Assert.DoesNotContain(first, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("passphrase", text, StringComparison.OrdinalIgnoreCase);
        }

        f.Ready();
        Assert.NotEqual(first, f.Platform.LastPassphraseForTests);
    }

    [Fact]
    public void Encryption_is_polled_until_fully_encrypted()
    {
        using var f = new VaultFixture();
        f.Platform.EncryptionPolls = 3;
        f.Ready();
        Assert.Equal(4, f.Platform.CallCount("query"));
    }

    [Fact]
    public void Missing_prerequisites_refuse_before_anything_is_created()
    {
        using var f = new VaultFixture();
        f.Platform.Inject("prereq", PlatformStatus.Unsupported);
        VaultOutcome o = f.NewService().Init(VaultFixture.Owner);
        Assert.Equal(ExitCode.Unsupported, o.Code);
        Assert.Null(o.Evidence.VaultId);
        Assert.Empty(f.Journal.ListIds());
        Assert.Equal(0, f.Platform.CallCount("create"));
    }

    [Fact]
    public void A_policy_refusal_is_unsupported_and_the_new_image_is_undone()
    {
        using var f = new VaultFixture();
        f.Platform.Inject("protect", PlatformStatus.PolicyRefused);
        VaultOutcome o = f.NewService().Init(VaultFixture.Owner);

        Assert.Equal(ExitCode.Unsupported, o.Code);
        Assert.Equal(VaultPhases.Retired, o.Evidence.Phase);
        Assert.Null(o.Evidence.Mount);
        VaultRecord rec = f.Only();
        Assert.Null(f.Platform.Image(rec.BackingPath));
        Assert.Equal(0, f.Platform.CallCount("mount"));
        Assert.Equal(0, f.Platform.CallCount("encrypt"));
    }

    [Theory]
    [InlineData("extra-protector")]
    [InlineData("auto-unlock")]
    public void A_failed_readiness_gate_never_publishes_a_folder(string defect)
    {
        using var f = new VaultFixture();
        if (defect == "extra-protector") f.Platform.ProtectorTypes = [8, 3];
        else f.Platform.AutoUnlock = true;

        VaultOutcome o = f.NewService().Init(VaultFixture.Owner);
        Assert.Equal(ExitCode.Failed, o.Code);
        Assert.Equal(VaultPhases.Retired, o.Evidence.Phase);
        Assert.Equal(0, f.Platform.CallCount("mount"));
        Assert.Null(f.Platform.Image(f.Only().BackingPath));
    }

    [Fact]
    public void A_failure_whose_cleanup_cannot_be_verified_becomes_an_orphan_for_review()
    {
        using var f = new VaultFixture();
        f.Platform.Inject("encrypt", PlatformStatus.Failed);
        f.Platform.Inject("delete", PlatformStatus.Failed);
        VaultOutcome o = f.NewService().Init(VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.Equal(VaultPhases.Orphan, o.Evidence.Phase);
        Assert.NotNull(f.Only().ReviewReason);
    }
}

public class VaultCloseTests
{
    [Fact]
    public void Close_locks_verifies_detaches_and_retires()
    {
        using var f = new VaultFixture();
        var (service, id, rec) = f.Ready();
        var phases = new List<string>();
        f.Platform.Calls.Clear();

        VaultOutcome o = service.Close(id, VaultFixture.Owner, phases.Add);

        Assert.Equal(ExitCode.Completed, o.Code);
        Assert.Equal(VaultPhases.Retired, o.Evidence.Phase);
        Assert.Null(o.Evidence.Mount);
        Assert.True(o.Evidence.LockedObserved);
        Assert.Equal(["locking"], phases);
        Assert.Equal(["quiesce", "unmount", "lock", "lockstatus", "detach", "delete"], f.Platform.Calls);
        Assert.Null(f.Platform.Image(rec.BackingPath));
        Assert.False(service.OwnsLiveSession(id));

        VaultRecord after = f.Journal.Load(id)!;
        Assert.NotNull(after.Receipt);
        Assert.True(after.Receipt!.LockedObserved);
    }

    [Fact]
    public void A_busy_vault_stays_open_and_can_be_closed_later()
    {
        using var f = new VaultFixture();
        var (service, id, rec) = f.Ready();
        f.Platform.SetOpenHandles(rec.BackingPath, true);

        VaultOutcome busy = service.Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Busy, busy.Code);
        Assert.Equal(VaultPhases.Ready, busy.Evidence.Phase);
        Assert.NotNull(busy.Evidence.Mount);
        Assert.Equal(0, f.Platform.CallCount("unmount"));
        Assert.Equal(0, f.Platform.CallCount("lock"));
        Assert.True(f.Platform.Image(rec.BackingPath)!.Attached);

        f.Platform.SetOpenHandles(rec.BackingPath, false);
        Assert.Equal(ExitCode.Completed, service.Close(id, VaultFixture.Owner).Code);
    }

    [Fact]
    public void A_refused_lock_leaves_the_vault_unavailable_and_the_close_can_be_retried()
    {
        using var f = new VaultFixture();
        var (service, id, rec) = f.Ready();
        f.Platform.Inject("lock", PlatformStatus.RecoveryKeyRequired);

        VaultOutcome stuck = service.Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, stuck.Code);
        Assert.Equal(VaultPhases.Closing, stuck.Evidence.Phase);
        Assert.Null(stuck.Evidence.Mount);
        Assert.NotNull(f.Platform.Image(rec.BackingPath));
        Assert.Equal(0, f.Platform.CallCount("delete"));

        // The owner is still alive. A second close resumes the same transition.
        VaultOutcome retry = service.Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Completed, retry.Code);
        Assert.True(retry.Evidence.LockedObserved);
    }

    [Fact]
    public void A_resumed_close_that_finds_the_volume_busy_never_reports_ready_again()
    {
        using var f = new VaultFixture();
        var (service, id, rec) = f.Ready();
        f.Platform.Inject("lock", PlatformStatus.RecoveryKeyRequired);
        Assert.Equal(ExitCode.Recovery, service.Close(id, VaultFixture.Owner).Code); // mount already removed

        f.Platform.SetOpenHandles(rec.BackingPath, true);
        VaultOutcome retry = service.Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, retry.Code);
        Assert.Equal(VaultPhases.Closing, retry.Evidence.Phase);
        Assert.Null(retry.Evidence.Mount);
        Assert.Equal(VaultPhases.Closing, f.Journal.Load(id)!.Phase);

        f.Platform.SetOpenHandles(rec.BackingPath, false);
        Assert.Equal(ExitCode.Completed, service.Close(id, VaultFixture.Owner).Code);
    }

    [Fact]
    public void The_qualified_detach_route_observes_the_lock_on_private_reattach()
    {
        using var f = new VaultFixture();
        var (service, id, rec) = f.Ready(detachRoute: true);
        f.Platform.Inject("lock", PlatformStatus.RecoveryKeyRequired);
        f.Platform.Calls.Clear();

        VaultOutcome o = service.Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Completed, o.Code);
        Assert.True(o.Evidence.LockedObserved);
        Assert.Equal(["quiesce", "unmount", "lock", "detach", "attach", "find", "lockstatus", "detach", "delete"], f.Platform.Calls);
        Assert.Null(f.Platform.Image(rec.BackingPath));
    }

    [Fact]
    public void The_detach_route_fails_closed_when_the_reattached_volume_is_not_locked()
    {
        using var f = new VaultFixture();
        var (service, id, rec) = f.Ready(detachRoute: true);
        f.Platform.Inject("lock", PlatformStatus.RecoveryKeyRequired);
        f.Platform.AutoUnlock = true; // the image comes back unlocked

        VaultOutcome o = service.Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.NotNull(f.Platform.Image(rec.BackingPath));
        Assert.Equal(0, f.Platform.CallCount("delete"));
    }

    [Fact]
    public void Closing_a_retired_vault_again_returns_the_same_receipt_without_touching_anything()
    {
        using var f = new VaultFixture();
        var (service, id, _) = f.Ready();
        VaultOutcome first = service.Close(id, VaultFixture.Owner);
        int callsAfterFirst = f.Platform.Calls.Count;

        VaultOutcome again = service.Close(id, VaultFixture.Owner);
        VaultOutcome fromAnotherProcess = f.NewService().Close(id, VaultFixture.Owner);

        Assert.Equal(first, again with { });
        Assert.Equal(ExitCode.Completed, fromAnotherProcess.Code);
        Assert.Equal(VaultPhases.Retired, fromAnotherProcess.Evidence.Phase);
        Assert.True(fromAnotherProcess.Evidence.LockedObserved);
        Assert.Equal(callsAfterFirst, f.Platform.Calls.Count);
    }

    [Fact]
    public void Two_simultaneous_closes_run_one_transition_and_both_get_the_receipt()
    {
        using var f = new VaultFixture();
        var (service, id, _) = f.Ready();
        using var start = new ManualResetEventSlim();

        Task<VaultOutcome>[] closers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            return service.Close(id, VaultFixture.Owner);
        })).ToArray();
        start.Set();
        Task.WaitAll(closers);

        Assert.All(closers, t => Assert.Equal(ExitCode.Completed, t.Result.Code));
        Assert.All(closers, t => Assert.Equal(VaultPhases.Retired, t.Result.Evidence.Phase));
        Assert.Equal(1, f.Platform.CallCount("lock"));
        Assert.Equal(1, f.Platform.CallCount("delete"));
    }

    [Fact]
    public void Only_the_recorded_owner_may_query_or_close()
    {
        using var f = new VaultFixture();
        var (service, id, rec) = f.Ready();

        Assert.Equal(ExitCode.Denied, service.Status(id, VaultFixture.Stranger).Code);
        Assert.Equal(VaultPhases.Unknown, service.Status(id, VaultFixture.Stranger).Evidence.Phase);
        Assert.Equal(ExitCode.Denied, service.Close(id, VaultFixture.Stranger).Code);
        Assert.Equal(ExitCode.Denied, service.Status(Guid.NewGuid(), VaultFixture.Owner).Code);
        Assert.Equal(ExitCode.Denied, service.Close(Guid.NewGuid(), VaultFixture.Owner).Code);

        Assert.True(f.Platform.Image(rec.BackingPath)!.Attached);
        VaultOutcome status = service.Status(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Completed, status.Code);
        Assert.Equal(VaultPhases.Ready, status.Evidence.Phase);
        Assert.NotNull(status.Evidence.Ready);
    }

    [Fact]
    public void A_process_that_is_not_the_owner_cannot_disturb_a_live_vault()
    {
        using var f = new VaultFixture();
        var (_, id, rec) = f.Ready();

        VaultOutcome o = f.NewService().Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.Equal(VaultPhases.Ready, f.Journal.Load(id)!.Phase);
        Assert.True(f.Platform.Image(rec.BackingPath)!.Attached);
        Assert.Equal(0, f.Platform.CallCount("delete"));
    }
}

public class VaultRecoveryTests
{
    [Theory]
    [InlineData("create", false)]
    [InlineData("attach", false)]
    [InlineData("attach", true)]
    [InlineData("format", false)]
    [InlineData("format", true)]
    [InlineData("protect", true)]
    [InlineData("encrypt", true)]
    [InlineData("query", true)]
    [InlineData("mount", false)]
    [InlineData("mount", true)]
    public void A_worker_killed_during_provisioning_is_reconciled_to_a_retired_vault(string step, bool afterEffect)
    {
        using var f = new VaultFixture();
        f.Platform.InjectCrash(step, afterEffect);
        Assert.Throws<SimulatedTerminationException>(() => f.NewService().Init(VaultFixture.Owner));
        f.Platform.OwnerProcessExited();

        VaultRecord before = f.Only();
        Assert.Equal(VaultPhases.Creating, before.Phase);
        Assert.False(before.StepDone);

        VaultOutcome o = Assert.Single(f.NewService().ReconcileAll());
        Assert.Equal(ExitCode.Completed, o.Code);
        Assert.Equal(VaultPhases.Retired, o.Evidence.Phase);
        Assert.Null(f.Platform.Image(before.BackingPath));

        // Recovery is idempotent: a second pass finds nothing to do.
        Assert.Empty(f.NewService().ReconcileAll());
    }

    [Fact]
    public void An_image_whose_identity_was_never_recorded_is_preserved_for_review()
    {
        using var f = new VaultFixture();
        f.Platform.InjectCrash("create", afterEffect: true);
        Assert.Throws<SimulatedTerminationException>(() => f.NewService().Init(VaultFixture.Owner));
        f.Platform.OwnerProcessExited();

        VaultOutcome o = Assert.Single(f.NewService().ReconcileAll());
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.Equal(VaultPhases.Orphan, o.Evidence.Phase);
        VaultRecord rec = f.Only();
        Assert.NotNull(f.Platform.Image(rec.BackingPath));
        Assert.Equal(0, f.Platform.CallCount("delete"));
    }

    [Theory]
    [InlineData("quiesce", false)]
    [InlineData("unmount", true)]
    [InlineData("lock", false)]
    [InlineData("lock", true)]
    [InlineData("detach", true)]
    [InlineData("delete", false)]
    public void An_interrupted_close_is_finished_by_recovery(string step, bool afterEffect)
    {
        using var f = new VaultFixture();
        var (service, id, rec) = f.Ready();
        f.Platform.InjectCrash(step, afterEffect);
        Assert.Throws<SimulatedTerminationException>(() => service.Close(id, VaultFixture.Owner));
        f.Platform.OwnerProcessExited();
        Assert.Equal(VaultPhases.Closing, f.Journal.Load(id)!.Phase);

        var phases = new List<string>();
        VaultOutcome o = f.NewService().Close(id, VaultFixture.Owner, phases.Add);
        Assert.Equal(ExitCode.Completed, o.Code);
        Assert.Equal(VaultPhases.Retired, o.Evidence.Phase);
        Assert.True(o.Evidence.LockedObserved);
        Assert.Equal(["reconciling"], phases);
        Assert.Null(f.Platform.Image(rec.BackingPath));
        Assert.Equal(1, f.Platform.CallCount("delete") - (step == "delete" ? 1 : 0));
    }

    [Fact]
    public void An_interrupted_delete_that_already_happened_reconciles_to_retired()
    {
        using var f = new VaultFixture();
        var (service, id, _) = f.Ready();
        f.Platform.InjectCrash("delete", afterEffect: true);
        Assert.Throws<SimulatedTerminationException>(() => service.Close(id, VaultFixture.Owner));
        f.Platform.OwnerProcessExited();

        VaultOutcome o = f.NewService().Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Completed, o.Code);
        Assert.Equal(VaultPhases.Retired, o.Evidence.Phase);
        Assert.True(o.Evidence.LockedObserved);
    }

    [Fact]
    public void Without_opt_in_an_orphan_is_reported_and_left_alone()
    {
        using var f = new VaultFixture();
        var (_, id, rec) = f.Ready(optIn: false);
        f.Platform.OwnerProcessExited();

        VaultService later = f.NewService(optIn: true); // the choice recorded at creation wins
        VaultOutcome o = later.Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.Equal(VaultPhases.Orphan, o.Evidence.Phase);
        Assert.NotNull(f.Platform.Image(rec.BackingPath));

        VaultOutcome status = later.Status(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Completed, status.Code);
        Assert.Equal(VaultPhases.Orphan, status.Evidence.Phase);
        Assert.Null(status.Evidence.Mount);
    }

    [Fact]
    public void A_mount_folder_that_resolves_to_another_volume_blocks_cleanup()
    {
        using var f = new VaultFixture();
        var (_, id, rec) = f.Ready();
        f.Platform.OwnerProcessExited();
        f.Platform.SetMount(rec.MountPath, @"\\?\Volume{00000000-0000-0000-0000-000000000001}\");

        VaultOutcome o = f.NewService().Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.Equal(VaultPhases.Orphan, o.Evidence.Phase);
        Assert.NotNull(f.Platform.Image(rec.BackingPath));
        Assert.Equal(0, f.Platform.CallCount("delete"));
        Assert.Contains("different volume", f.Journal.Load(id)!.ReviewReason);
    }

    [Fact]
    public void A_backing_file_with_a_different_identity_is_never_deleted()
    {
        using var f = new VaultFixture();
        var (_, id, rec) = f.Ready();
        f.Platform.OwnerProcessExited();
        f.Platform.ReplaceBackingFile(rec.BackingPath);

        VaultOutcome o = f.NewService().Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.Equal(VaultPhases.Orphan, o.Evidence.Phase);
        Assert.NotNull(f.Platform.Image(rec.BackingPath));
        Assert.Equal(0, f.Platform.CallCount("delete"));
    }

    [Fact]
    public void Open_handles_block_recovery()
    {
        using var f = new VaultFixture();
        var (_, id, rec) = f.Ready();
        f.Platform.OwnerProcessExited();
        f.Platform.SetOpenHandles(rec.BackingPath, true);

        VaultOutcome o = f.NewService().Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.NotNull(f.Platform.Image(rec.BackingPath));
    }

    [Fact]
    public void Recovery_requires_a_locked_observation_for_a_vault_that_was_published()
    {
        using var f = new VaultFixture();
        var (_, id, rec) = f.Ready();
        f.Platform.OwnerProcessExited();
        f.Platform.AutoUnlock = true; // reattach would come up unlocked

        VaultOutcome o = f.NewService().Close(id, VaultFixture.Owner);
        Assert.Equal(ExitCode.Recovery, o.Code);
        Assert.Equal(VaultPhases.Orphan, o.Evidence.Phase);
        Assert.NotNull(f.Platform.Image(rec.BackingPath));
        Assert.False(f.Platform.Image(rec.BackingPath)!.Attached);
    }
}

public class VaultJournalTests
{
    [Fact]
    public void Commits_are_sequenced_and_leave_no_temporary_file()
    {
        using var dir = new TempDir();
        var journal = new VaultJournal(dir.Path);
        var rec = new VaultRecord { VaultId = Guid.NewGuid(), OwnerSid = VaultFixture.Owner };
        journal.Commit(rec);
        rec.Phase = VaultPhases.Ready;
        journal.Commit(rec);

        VaultRecord loaded = journal.Load(rec.VaultId)!;
        Assert.Equal(2, loaded.CommitSequence);
        Assert.Equal(VaultPhases.Ready, loaded.Phase);
        Assert.Equal([rec.VaultId], journal.ListIds());
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.new"));
        Assert.Null(journal.Load(Guid.NewGuid()));
    }

    [Fact]
    public void A_corrupt_record_is_reported_and_never_guessed_at()
    {
        using var dir = new TempDir();
        var journal = new VaultJournal(dir.Path);
        Guid id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(dir.Path, id.ToString("D") + ".json"), "{ not json");
        Assert.Throws<JournalCorruptException>(() => journal.Load(id));

        var service = new VaultService(journal, new SimulatedVaultPlatform(),
            new VaultOptions { ImageDirectory = dir.Path, MountRoot = dir.Path });
        Assert.Equal(ExitCode.Recovery, service.Status(id, VaultFixture.Owner).Code);
        Assert.Equal(ExitCode.Recovery, service.Close(id, VaultFixture.Owner).Code);
    }

    [Fact]
    public void The_vault_lock_admits_one_holder_at_a_time()
    {
        using var dir = new TempDir();
        var journal = new VaultJournal(dir.Path);
        Guid id = Guid.NewGuid();
        using (IDisposable? first = journal.TryLock(id, TimeSpan.FromSeconds(1)))
        {
            Assert.NotNull(first);
            Assert.Null(journal.TryLock(id, TimeSpan.FromMilliseconds(100)));
        }
        using IDisposable? second = journal.TryLock(id, TimeSpan.FromSeconds(1));
        Assert.NotNull(second);
    }
}
