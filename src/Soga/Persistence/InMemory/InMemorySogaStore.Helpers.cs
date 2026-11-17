using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// Shared lookups and builders used by the operations of <see cref="InMemorySogaStore"/>.
/// </summary>
internal sealed partial class InMemorySogaStore
{
    /// <summary>
    /// Appends a system message to a conversation and publishes its <c>MessageCreated</c> event. The caller must hold
    /// <see cref="_gate"/>.
    /// </summary>
    /// <param name="conversation">The conversation to append to.</param>
    /// <param name="tenantId">The tenant of the conversation.</param>
    /// <param name="ids">Generates the message and event IDs.</param>
    /// <param name="content">The system message body.</param>
    /// <param name="now">The current time.</param>
    private void AppendSystemMessage(
        ConversationState conversation,
        string tenantId,
        Abstractions.ISogaIdGenerator ids,
        string content,
        DateTimeOffset now)
    {
        var messageId = ids.NewId();
        conversation.LastSequence++;
        conversation.LastActivityAt = now;
        var message = new MessageRecord(
            messageId,
            conversation.Id,
            conversation.LastSequence,
            SogaSystemSender.UserId,
            $"sys:{messageId:N}",
            content,
            now,
            SogaMessageKinds.System,
            SentFingerprint: SogaText.Fingerprint(content));
        conversation.Messages.Add(message);
        Publish(tenantId, SogaEventFactory.MessageCreated(ids.NewId(), message, now));
    }

    /// <summary>
    /// Appends an event to the tenant's change log and to the outbox, as part of the current operation. The caller
    /// must hold <see cref="_gate"/>.
    /// </summary>
    /// <param name="tenantId">The tenant of the event.</param>
    /// <param name="evt">The event to record.</param>
    /// <param name="targetUserId">A user who must see the change in sync even without access to the conversation.</param>
    private void Publish(string tenantId, SogaEvent evt, string? targetUserId = null)
    {
        var payload = SogaEventSerializer.Serialize(evt);

        if (!_changes.TryGetValue(tenantId, out var list))
        {
            list = [];
            _changes[tenantId] = list;
        }

        var sequence = list.Count == 0 ? 1 : list[^1].Sequence + 1;
        list.Add(new ChangeEntry(sequence, evt.EventId, evt.ConversationId, payload, targetUserId));
        _outbox.Add(new OutboxEntry(evt.EventId, tenantId, evt.Type, payload, evt.OccurredAt));
    }

    /// <summary>
    /// Finds a conversation that a user is an active participant of.
    /// </summary>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="userId">The user.</param>
    /// <param name="conversationId">The ID of the conversation.</param>
    /// <returns>
    /// The conversation, or <see langword="null"/> when it does not exist or the user is not an active participant.
    /// </returns>
    private ConversationState? FindAccessible(string tenantId, string userId, Guid conversationId) =>
        _conversations.TryGetValue((tenantId, conversationId), out var conversation) &&
        IsActiveParticipant(conversation, userId)
            ? conversation
            : null;

    /// <summary>
    /// Determines whether a user is an active participant of a conversation.
    /// </summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="userId">The user.</param>
    /// <returns><see langword="true"/> if the user is a participant who has not left; otherwise, <see langword="false"/>.</returns>
    private static bool IsActiveParticipant(ConversationState conversation, string userId) =>
        conversation.Participants.Any(p => p.UserId == userId && p.LeftAt is null);

    /// <summary>
    /// Counts a participant's unread messages in a conversation.
    /// </summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="participant">The participant.</param>
    /// <returns>
    /// The number of undeleted messages after the participant's read position that were sent by someone else.
    /// </returns>
    private static long Unread(ConversationState conversation, ParticipantState participant) =>
        conversation.Messages.Count(m =>
            m.Sequence > participant.LastReadSequence && m.SenderId != participant.UserId && !m.IsDeleted);

    /// <summary>
    /// Adds the reply summary to a message that replies to another.
    /// </summary>
    /// <param name="conversation">The conversation that holds both messages.</param>
    /// <param name="message">The message to complete.</param>
    /// <returns>The message with <see cref="MessageRecord.ReplyTo"/> filled in, or unchanged if it is not a reply.</returns>
    private static MessageRecord Hydrate(ConversationState conversation, MessageRecord message)
    {
        if (message.ReplyToMessageId is not { } replyId)
        {
            return message;
        }

        var target = conversation.Messages.FirstOrDefault(m => m.Id == replyId);
        return target is null
            ? message
            : message with { ReplyTo = new ReplyInfo(target.Id, target.SenderId, SogaText.Snippet(target.Content), target.IsDeleted) };
    }

    /// <summary>
    /// Builds the record of a conversation as seen by one participant.
    /// </summary>
    /// <param name="conversation">The conversation.</param>
    /// <param name="userId">The viewing participant, who must be active.</param>
    /// <param name="includeParticipants"><see langword="true"/> to include the participant list.</param>
    /// <returns>The conversation record, with the viewer's role, read position, and unread count.</returns>
    private static ConversationRecord ToRecord(ConversationState conversation, string userId, bool includeParticipants)
    {
        var mine = conversation.Participants.Single(p => p.UserId == userId && p.LeftAt is null);
        var active = conversation.Participants.Where(p => p.LeftAt is null).ToList();
        var last = conversation.Messages.Count == 0 ? null : conversation.Messages.MaxBy(m => m.Sequence);

        return new ConversationRecord(
            conversation.Id,
            conversation.Kind,
            conversation.Title,
            conversation.ExternalReference,
            conversation.MetadataJson,
            includeParticipants
                ? [.. active.OrderBy(p => p.UserId, StringComparer.Ordinal).Select(p => new ParticipantInfo(p.UserId, p.Role, p.JoinedAt))]
                : [],
            active.Count,
            conversation.CreatedAt,
            conversation.LastActivityAt,
            conversation.ArchivedAt,
            conversation.LastSequence,
            mine.LastReadSequence,
            Unread(conversation, mine),
            mine.Role,
            last is null
                ? null
                : new MessageSummary(last.Id, last.Sequence, last.SenderId, last.Kind, SogaText.Snippet(last.Content), last.CreatedAt, last.IsDeleted));
    }
}
