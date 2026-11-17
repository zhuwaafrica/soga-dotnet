using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// Conversation creation, lookup, and listing for <see cref="InMemorySogaStore"/>.
/// </summary>
internal sealed partial class InMemorySogaStore
{
    /// <inheritdoc />
    public Task<DirectConversationOutcome> GetOrCreateDirectConversationAsync(
        CreateDirectConversationCommand command,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = DirectConversationKey.Create(command.UserId, command.OtherUserId, command.ExternalReference);

        lock (_gate)
        {
            if (_directIndex.TryGetValue((command.TenantId, key), out var existingId))
            {
                var existing = _conversations[(command.TenantId, existingId)];
                return Task.FromResult(new DirectConversationOutcome(ToRecord(existing, command.UserId, includeParticipants: true), false));
            }

            var conversation = new ConversationState(
                command.ConversationId,
                command.TenantId,
                SogaConversationKinds.Direct,
                title: null,
                command.ExternalReference,
                command.MetadataJson,
                command.Now);
            foreach (var participant in new[] { command.UserId, command.OtherUserId }.Order(StringComparer.Ordinal))
            {
                conversation.Participants.Add(new ParticipantState(participant, SogaRole.Member, command.Now));
            }

            _conversations[(command.TenantId, conversation.Id)] = conversation;
            _directIndex[(command.TenantId, key)] = conversation.Id;

            Publish(
                command.TenantId,
                SogaEventFactory.ConversationCreated(
                    command.EventId,
                    conversation.Id,
                    [.. conversation.Participants.Select(p => p.UserId)],
                    conversation.ExternalReference,
                    command.Now));

            return Task.FromResult(new DirectConversationOutcome(ToRecord(conversation, command.UserId, includeParticipants: true), true));
        }
    }

    /// <inheritdoc />
    public Task<ConversationRecord?> GetConversationAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(tenantId, userId, conversationId);
            return Task.FromResult(conversation is null ? null : ToRecord(conversation, userId, includeParticipants: true));
        }
    }

    /// <inheritdoc />
    public Task<ConversationAccess?> GetAccessAsync(
        string tenantId,
        string userId,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = FindAccessible(tenantId, userId, conversationId);
            var access = conversation is null
                ? null
                : new ConversationAccess(
                    conversation.Kind,
                    conversation.Participants.Single(p => p.UserId == userId && p.LeftAt is null).Role,
                    conversation.ArchivedAt is not null,
                    conversation.ExternalReference);
            return Task.FromResult(access);
        }
    }

    /// <inheritdoc />
    public Task<ConversationPage> ListConversationsAsync(ListConversationsQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var matches = _conversations.Values
                .Where(c => c.TenantId == query.TenantId && IsActiveParticipant(c, query.UserId))
                .Where(c => query.Kind is null || c.Kind == query.Kind)
                .Where(c => query.ExternalReference is null || c.ExternalReference == query.ExternalReference)
                .Where(c => query.State switch
                {
                    ConversationStateFilter.Active => c.ArchivedAt is null,
                    ConversationStateFilter.Archived => c.ArchivedAt is not null,
                    _ => true,
                })
                .Where(c => query.AfterActivity is null || c.LastActivityAt <= query.AfterActivity)
                .OrderByDescending(c => c.LastActivityAt)
                .ThenByDescending(c => c.Id)
                .Skip(query.SkipTies)
                .Take(query.Limit + 1)
                .ToList();

            var hasMore = matches.Count > query.Limit;
            var items = matches
                .Take(query.Limit)
                .Select(c => ToRecord(c, query.UserId, includeParticipants: c.Kind == SogaConversationKinds.Direct))
                .ToList();
            return Task.FromResult(new ConversationPage(items, hasMore));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Guid>> ListActiveConversationIdsAsync(
        string tenantId,
        string userId,
        int limit,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            IReadOnlyList<Guid> ids = _conversations.Values
                .Where(c => c.TenantId == tenantId && IsActiveParticipant(c, userId))
                .OrderByDescending(c => c.LastActivityAt)
                .ThenByDescending(c => c.Id)
                .Take(limit)
                .Select(c => c.Id)
                .ToList();
            return Task.FromResult(ids);
        }
    }
}
