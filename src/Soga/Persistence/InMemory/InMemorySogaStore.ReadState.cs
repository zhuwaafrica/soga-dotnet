using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// Read positions and unread counts for <see cref="InMemorySogaStore"/>.
/// </summary>
internal sealed partial class InMemorySogaStore
{
    /// <inheritdoc />
    public Task<AdvanceReadOutcome> AdvanceReadPositionAsync(
        AdvanceReadCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(command.TenantId, command.UserId, command.ConversationId);
            if (conversation is null)
            {
                return Task.FromResult(new AdvanceReadOutcome(AdvanceStatus.ConversationNotFound, 0, 0));
            }

            var message = conversation.Messages.FirstOrDefault(m => m.Id == command.MessageId);
            if (message is null)
            {
                return Task.FromResult(new AdvanceReadOutcome(AdvanceStatus.MessageNotFound, 0, 0));
            }

            var participant = conversation.Participants.Single(p => p.UserId == command.UserId && p.LeftAt is null);
            var advanced = message.Sequence > participant.LastReadSequence;
            if (advanced)
            {
                participant.LastReadSequence = message.Sequence;
                Publish(
                    command.TenantId,
                    SogaEventFactory.ReadPositionChanged(
                        command.EventId,
                        conversation.Id,
                        command.UserId,
                        participant.LastReadSequence,
                        command.Now));
            }

            return Task.FromResult(new AdvanceReadOutcome(
                advanced ? AdvanceStatus.Advanced : AdvanceStatus.Unchanged,
                participant.LastReadSequence,
                Unread(conversation, participant)));
        }
    }

    /// <inheritdoc />
    public Task<long> GetTotalUnreadAsync(string tenantId, string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var total = _conversations.Values
                .Where(c => c.TenantId == tenantId && IsActiveParticipant(c, userId))
                .Sum(c => Unread(c, c.Participants.Single(p => p.UserId == userId && p.LeftAt is null)));
            return Task.FromResult(total);
        }
    }
}
