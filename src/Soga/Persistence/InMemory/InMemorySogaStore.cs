using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// The in-memory development implementation of <see cref="ISogaStore"/>, used when no durable store is configured.
/// </summary>
/// <remarks>
/// <para>
/// It offers the same guarantees as the Entity Framework Core store: atomic message, change, and outbox writes;
/// idempotency; gapless ordering; and the same group and message rules. All data is lost when the process stops.
/// </para>
/// <para>
/// A single lock serializes every operation, which is what makes each write atomic. That is simple and correct but
/// does not scale, so this store is meant for samples, tests, and local development only.
/// </para>
/// </remarks>
internal sealed partial class InMemorySogaStore : ISogaStore
{
    /// <summary>
    /// The lock that serializes every read and write, making each operation atomic.
    /// </summary>
    private readonly object _gate = new();

    /// <summary>
    /// Every conversation, keyed by tenant and conversation ID.
    /// </summary>
    private readonly Dictionary<(string Tenant, Guid Id), ConversationState> _conversations = [];

    /// <summary>
    /// The uniqueness index of direct conversations, keyed by tenant and <see cref="DirectConversationKey"/>.
    /// </summary>
    private readonly Dictionary<(string Tenant, string Key), Guid> _directIndex = [];

    /// <summary>
    /// The change log of each tenant, in sequence order.
    /// </summary>
    private readonly Dictionary<string, List<ChangeEntry>> _changes = [];

    /// <summary>
    /// The outbox of events waiting to be, or already, published.
    /// </summary>
    private readonly List<OutboxEntry> _outbox = [];

    /// <summary>
    /// The previous texts of edited messages, kept when edit history retention is on.
    /// </summary>
    private readonly List<EditAudit> _edits = [];

    /// <inheritdoc />
    public bool IsDurable => false;

    /// <summary>
    /// Gets the previous texts kept for a message when edit history retention is on.
    /// </summary>
    /// <param name="messageId">The ID of the message.</param>
    /// <returns>The previous texts, oldest first.</returns>
    /// <remarks>Exposed for tests and diagnostics.</remarks>
    internal IReadOnlyList<string> GetEditHistory(Guid messageId)
    {
        lock (_gate)
        {
            return
            [
                .. _edits.Where(e => e.MessageId == messageId).OrderBy(e => e.Version).Select(e => e.PreviousContent)
            ];
        }
    }

    /// <inheritdoc />
    /// <remarks>The in-memory store is always reachable.</remarks>
    public Task<bool> CheckHealthAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}
