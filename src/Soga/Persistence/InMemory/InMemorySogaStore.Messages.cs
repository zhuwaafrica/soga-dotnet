using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// Message append, edit, delete, and history for <see cref="InMemorySogaStore"/>.
/// </summary>
internal sealed partial class InMemorySogaStore
{
    /// <inheritdoc />
    public Task<AppendMessageOutcome> AppendMessageAsync(
        AppendMessageCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = command.IsSystem
                ? _conversations.GetValueOrDefault((command.TenantId, command.ConversationId))
                : FindAccessible(command.TenantId, command.UserId, command.ConversationId);
            if (conversation is null)
            {
                return Task.FromResult(new AppendMessageOutcome(AppendStatus.ConversationNotFound, null));
            }

            var existing = conversation.Messages.FirstOrDefault(m =>
                m.SenderId == command.UserId && m.ClientMessageId == command.ClientMessageId);
            if (existing is not null)
            {
                // Compare with the text as first sent, not the current text, so an edit does not break retries (ADR 0011).
                var status = existing.IsDeleted || SogaText.IsSameSend(existing.SentFingerprint, existing.Content, command.Content)
                    ? AppendStatus.Duplicate
                    : AppendStatus.IdempotencyConflict;
                return Task.FromResult(new AppendMessageOutcome(status, Hydrate(conversation, existing)));
            }

            if (conversation.ArchivedAt is not null && !command.IsSystem)
            {
                return Task.FromResult(new AppendMessageOutcome(AppendStatus.Archived, null));
            }

            if (command.ReplyToMessageId is { } replyId)
            {
                var target = conversation.Messages.FirstOrDefault(m => m.Id == replyId);
                if (target is null || target.IsDeleted)
                {
                    return Task.FromResult(new AppendMessageOutcome(AppendStatus.ReplyInvalid, null));
                }
            }

            conversation.LastSequence++;
            conversation.LastActivityAt = command.Now;
            var message = new MessageRecord(
                command.MessageId,
                conversation.Id,
                conversation.LastSequence,
                command.UserId,
                command.ClientMessageId,
                command.Content,
                command.Now,
                command.IsSystem ? SogaMessageKinds.System : SogaMessageKinds.Text,
                ReplyToMessageId: command.ReplyToMessageId,
                SentFingerprint: SogaText.Fingerprint(command.Content));
            conversation.Messages.Add(message);

            Publish(command.TenantId, SogaEventFactory.MessageCreated(command.EventId, Hydrate(conversation, message), command.Now));
            return Task.FromResult(new AppendMessageOutcome(AppendStatus.Created, Hydrate(conversation, message)));
        }
    }

    /// <inheritdoc />
    public Task<EditMessageOutcome> EditMessageAsync(EditMessageCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(command.TenantId, command.UserId, command.ConversationId);
            if (conversation is null)
            {
                return Task.FromResult(new EditMessageOutcome(EditStatus.ConversationNotFound, null));
            }

            var index = conversation.Messages.FindIndex(m => m.Id == command.MessageId);
            if (index < 0)
            {
                return Task.FromResult(new EditMessageOutcome(EditStatus.MessageNotFound, null));
            }

            var message = conversation.Messages[index];
            var check = MessageRules.CanEdit(
                message.Kind,
                message.SenderId,
                message.CreatedAt,
                message.IsDeleted,
                command.UserId,
                command.Now,
                command.EditWindow);
            if (check != EditCheck.Allowed)
            {
                var status = ToEditStatus(check);
                return Task.FromResult(new EditMessageOutcome(status, null));
            }

            if (string.Equals(message.Content, command.Text, StringComparison.Ordinal))
            {
                return Task.FromResult(new EditMessageOutcome(EditStatus.Unchanged, Hydrate(conversation, message)));
            }

            if (command.RetainHistory)
            {
                _edits.Add(new EditAudit(message.Id, _edits.Count(e => e.MessageId == message.Id) + 1, message.Content, command.Now));
            }

            var updated = message with { Content = command.Text, EditedAt = command.Now };
            conversation.Messages[index] = updated;
            Publish(command.TenantId, SogaEventFactory.MessageUpdated(command.EventId, Hydrate(conversation, updated), command.Now));
            return Task.FromResult(new EditMessageOutcome(EditStatus.Updated, Hydrate(conversation, updated)));
        }
    }

    /// <inheritdoc />
    public Task<DeleteMessageOutcome> DeleteMessageAsync(DeleteMessageCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(command.TenantId, command.UserId, command.ConversationId);
            if (conversation is null)
            {
                return Task.FromResult(new DeleteMessageOutcome(DeleteStatus.ConversationNotFound, false, null));
            }

            var index = conversation.Messages.FindIndex(m => m.Id == command.MessageId);
            if (index < 0)
            {
                return Task.FromResult(new DeleteMessageOutcome(DeleteStatus.MessageNotFound, false, null));
            }

            var message = conversation.Messages[index];
            var actor = conversation.Participants.Single(p => p.UserId == command.UserId && p.LeftAt is null);
            var check = MessageRules.CanDelete(
                message.Kind,
                message.SenderId,
                message.IsDeleted,
                command.UserId,
                actor.Role,
                conversation.Kind == SogaConversationKinds.Group,
                command.AllowOwnDeletion,
                command.AllowAdminDeletion);

            if (check != DeleteCheck.Allowed)
            {
                return Task.FromResult(new DeleteMessageOutcome(ToDeleteStatus(check), false, message.SenderId));
            }

            // The text is erased so a deleted message never exposes its content again. The row stays as a tombstone
            // so sequences remain contiguous and replies can say "deleted".
            var deleted = message with { Content = string.Empty, SentFingerprint = null, DeletedAt = command.Now, DeletedBy = command.UserId };
            conversation.Messages[index] = deleted;
            _edits.RemoveAll(e => e.MessageId == message.Id); // Retained edit history must not keep deleted text alive.
            Publish(command.TenantId, SogaEventFactory.MessageDeleted(command.EventId, deleted, command.UserId, command.Now));
            return Task.FromResult(new DeleteMessageOutcome(
                DeleteStatus.Deleted,
                !string.Equals(message.SenderId, command.UserId, StringComparison.Ordinal),
                message.SenderId));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MessageRecord>?> GetMessagesAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        long? beforeSequence,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(tenantId, userId, conversationId);
            if (conversation is null)
            {
                return Task.FromResult<IReadOnlyList<MessageRecord>?>(null);
            }

            IReadOnlyList<MessageRecord> page = conversation.Messages
                .Where(m => beforeSequence is null || m.Sequence < beforeSequence)
                .OrderByDescending(m => m.Sequence)
                .Take(limit)
                .Select(m => Hydrate(conversation, m))
                .ToList();
            return Task.FromResult<IReadOnlyList<MessageRecord>?>(page);
        }
    }

    /// <summary>Maps a failed edit rule check to the status returned by the store.</summary>
    private static EditStatus ToEditStatus(EditCheck check) => check switch
    {
        EditCheck.Forbidden => EditStatus.Forbidden,
        EditCheck.WindowExpired => EditStatus.WindowExpired,
        _ => EditStatus.NotEditable,
    };

    /// <summary>Maps a failed delete rule check to the status returned by the store.</summary>
    private static DeleteStatus ToDeleteStatus(DeleteCheck check) => check switch
    {
        DeleteCheck.Forbidden => DeleteStatus.Forbidden,
        DeleteCheck.AlreadyDeleted => DeleteStatus.AlreadyDeleted,
        _ => DeleteStatus.NotDeletable,
    };
}
