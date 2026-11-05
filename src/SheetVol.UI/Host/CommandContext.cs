using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace SheetVol.UI.Host;

/// <summary>
/// Runs work for the modeless dialog in the active document's command context under a
/// document lock (HANDOFF.md 6.14, document rules). Call on the main thread.
/// <para>
/// Route: <see cref="DocumentCollection.ExecuteInCommandContextAsync"/>. The dialog's
/// handlers run in the application context (a WPF event on the main thread, no command
/// active); this call makes AutoCAD run the callback as if it were a command, so prompts
/// (<c>GetEntity</c>, <c>GetPoint</c>), view changes and database writes behave as they do in a
/// command. It was chosen over <c>SendStringToExecute</c> with a helper command because that
/// route is fire-and-forget (the result needs a second channel), echoes on the command line,
/// and queues behind whatever runs; it needs no command name and no string escaping.
/// </para>
/// <para>
/// The result travels through a <see cref="TaskCompletionSource{TResult}"/> completed when the
/// work finishes, so it does not depend on whether AutoCAD's own Task waits for an
/// asynchronous callback (not verified). Continuations of awaits inside the work run on the
/// main thread (a <see cref="DispatcherSynchronizationContext"/> is current while the work
/// starts), and the document lock is taken and released there. The callback refuses to run
/// when the active document changed after the request was queued. When the caller is already
/// in a document context the work runs in place under the command's lock, because nesting
/// <c>ExecuteInCommandContextAsync</c> is not allowed.
/// </para>
/// </summary>
internal static class CommandContext
{
    /// <summary>Runs <paramref name="work"/> in command context with the active document locked.</summary>
    /// <exception cref="InvalidOperationException">No drawing is open, or the active drawing changed before the work ran.</exception>
    public static async Task<T> RunAsync<T>(Func<Document, Task<T>> work)
    {
        var dm = AcApp.DocumentManager;
        var doc = dm.MdiActiveDocument ?? throw new InvalidOperationException(HostText.NoDrawing);
        if (!dm.IsApplicationContext)
        {
            using (doc.LockDocument()) return await work(doc);
        }

        var mainContext = new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await dm.ExecuteInCommandContextAsync(_ =>
        {
            if (!ReferenceEquals(dm.MdiActiveDocument, doc))
            {
                completion.TrySetException(new InvalidOperationException(HostText.DrawingChanged));
                return Task.CompletedTask;
            }
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(mainContext);
            try
            {
                _ = RunLockedAsync(doc, work, completion);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
            return completion.Task;
        }, null);
        return await completion.Task;
    }

    /// <summary>Synchronous work in command context; see <see cref="RunAsync{T}(Func{Document, Task{T}})"/>.</summary>
    public static Task<T> RunAsync<T>(Func<Document, T> work) => RunAsync(doc => Task.FromResult(work(doc)));

    /// <summary>
    /// Runs a synchronous read on the main thread with the active document locked: the route
    /// for the synchronous session members (Validate, LoadRun, object lists, station text,
    /// tracker status). In a document context the command already holds the lock.
    /// </summary>
    public static T Locked<T>(Func<T> read)
    {
        var dm = AcApp.DocumentManager;
        var doc = dm.MdiActiveDocument;
        if (doc is null || !dm.IsApplicationContext) return read();
        using (doc.LockDocument()) return read();
    }

    /// <summary>Takes the lock, awaits the work and releases the lock, all on the main thread (the context current when it starts).</summary>
    private static async Task RunLockedAsync<T>(Document doc, Func<Document, Task<T>> work, TaskCompletionSource<T> completion)
    {
        try
        {
            using (doc.LockDocument()) completion.TrySetResult(await work(doc));
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }
}
