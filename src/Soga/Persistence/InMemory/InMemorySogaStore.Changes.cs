using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// The change log used by sync, for <see cref="InMemorySogaStore"/>.
/// </summary>
internal sealed partial class InMemorySogaStore
{
    /// <inheritdoc />
    public Task<long> GetChangeHeadAsync(string tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var head = _changes.TryGetValue(tenantId, out var list) && list.Count > 0 ? list[^1].Sequence : 0;
            return Task.FromResult(head);
        }
    }

    /// <inheritdoc />
    public Task<ChangePage> GetChangesAsync(
        string tenantId,
        string userId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_changes.TryGetValue(tenantId, out var list))
            {
                return Task.FromResult(new ChangePage([], false));
            }

            var visible = list
                .Where(c => c.Sequence > afterSequence &&
                    (c.TargetUserId == userId ||
                        (_conversations.TryGetValue((tenantId, c.ConversationId), out var conversation) &&
                            IsActiveParticipant(conversation, userId))))
                .Take(limit + 1)
                .ToList();

            var hasMore = visible.Count > limit;
            var page = visible.Take(limit).Select(c => new ChangeRecord(c.Sequence, c.Payload)).ToList();
            return Task.FromResult(new ChangePage(page, hasMore));
        }
    }
}
