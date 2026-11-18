using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// Group creation and mutation for <see cref="InMemorySogaStore"/>.
/// </summary>
internal sealed partial class InMemorySogaStore
{
    /// <inheritdoc />
    public Task<ConversationRecord> CreateGroupAsync(CreateGroupCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var conversation = new ConversationState(
                command.ConversationId,
                command.TenantId,
                SogaConversationKinds.Group,
                command.Title,
                command.ExternalReference,
                command.MetadataJson,
                command.Now);

            conversation.Participants.Add(new ParticipantState(command.UserId, SogaRole.Owner, command.Now));
            foreach (var other in command.OtherParticipantIds.Distinct(StringComparer.Ordinal).Where(id => id != command.UserId))
            {
                conversation.Participants.Add(new ParticipantState(other, SogaRole.Member, command.Now));
            }

            _conversations[(command.TenantId, conversation.Id)] = conversation;
            Publish(
                command.TenantId,
                SogaEventFactory.ConversationCreated(
                    command.EventId,
                    conversation.Id,
                    [.. conversation.Participants.Select(p => p.UserId).Order(StringComparer.Ordinal)],
                    conversation.ExternalReference,
                    command.Now,
                    SogaConversationKinds.Group,
                    conversation.Title));

            return Task.FromResult(ToRecord(conversation, command.UserId, includeParticipants: true));
        }
    }

    /// <inheritdoc />
    public Task<GroupMutationOutcome> MutateGroupAsync(GroupMutationCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_conversations.TryGetValue((command.TenantId, command.ConversationId), out var conversation))
            {
                return Task.FromResult(new GroupMutationOutcome(GroupError.NotFound, false, null));
            }

            var snapshot = new GroupSnapshot(
                conversation.Kind == SogaConversationKinds.Group,
                conversation.ArchivedAt is not null,
                conversation.Title,
                conversation.MetadataJson,
                [.. conversation.Participants.Select(p => new ParticipantSnapshot(p.UserId, p.Role, p.LeftAt is null))]);

            var decision = GroupRules.Decide(snapshot, command.ActorId, command.Mutation, command.MaximumParticipants);
            if (decision.Error != GroupError.None)
            {
                return Task.FromResult(new GroupMutationOutcome(decision.Error, false, null));
            }

            if (!decision.Changed)
            {
                return Task.FromResult(new GroupMutationOutcome(GroupError.None, false, ToRecord(conversation, command.ActorId, includeParticipants: true)));
            }

            ApplyMutation(conversation, command);
            PublishMutationEvents(conversation, command, decision);

            var stillActive = conversation.Participants.Any(p => p.UserId == command.ActorId && p.LeftAt is null);
            return Task.FromResult(new GroupMutationOutcome(
                GroupError.None,
                true,
                stillActive ? ToRecord(conversation, command.ActorId, includeParticipants: true) : null));
        }
    }

    /// <summary>
    /// Applies an accepted group mutation to the conversation's state. The caller must hold <see cref="_gate"/>.
    /// </summary>
    /// <param name="conversation">The conversation to change.</param>
    /// <param name="command">The mutation command.</param>
    private static void ApplyMutation(ConversationState conversation, GroupMutationCommand command)
    {
        switch (command.Mutation)
        {
            case AddParticipantMutation add:
                AddOrRejoin(conversation, add, command.Now);
                break;
            case RemoveParticipantMutation remove:
                conversation.Participants.First(p => p.UserId == remove.UserId).LeftAt = command.Now;
                break;
            case ChangeRoleMutation change:
                conversation.Participants.First(p => p.UserId == change.UserId && p.LeftAt is null).Role = change.Role;
                break;
            case UpdateConversationMutation update:
                if (update.SetTitle)
                {
                    conversation.Title = update.Title;
                }

                if (update.SetMetadata)
                {
                    conversation.MetadataJson = update.MetadataJson;
                }

                break;
            case ArchiveMutation archive:
                conversation.ArchivedAt = archive.Archived ? command.Now : null;
                break;
        }
    }

    /// <summary>
    /// Adds a new participant, or brings back one who left, starting them at the current read position.
    /// </summary>
    /// <param name="conversation">The conversation to change.</param>
    /// <param name="add">The participant to add.</param>
    /// <param name="now">The current time.</param>
    private static void AddOrRejoin(ConversationState conversation, AddParticipantMutation add, DateTimeOffset now)
    {
        var existing = conversation.Participants.FirstOrDefault(p => p.UserId == add.UserId);
        if (existing is null)
        {
            conversation.Participants.Add(new ParticipantState(add.UserId, add.Role, now) { LastReadSequence = conversation.LastSequence });
            return;
        }

        existing.LeftAt = null;
        existing.Role = add.Role;
        existing.JoinedAt = now;
        existing.LastReadSequence = conversation.LastSequence;
    }

    /// <summary>
    /// Publishes the events a group decision planned, and the optional system message for each participant change.
    /// The caller must hold <see cref="_gate"/>.
    /// </summary>
    /// <param name="conversation">The conversation that changed.</param>
    /// <param name="command">The mutation command.</param>
    /// <param name="decision">The decision that holds the planned events.</param>
    private void PublishMutationEvents(ConversationState conversation, GroupMutationCommand command, GroupDecision decision)
    {
        var first = true;
        foreach (var plan in decision.Events)
        {
            var eventId = first ? command.EventId : command.Ids.NewId();
            first = false;
            Publish(
                command.TenantId,
                SogaEventFactory.FromPlan(eventId, conversation.Id, plan, command.Now),
                plan.TargetUserId);

            if (command.PostSystemMessage && plan.Data is ParticipantChangedData participantChange)
            {
                AppendSystemMessage(conversation, command.TenantId, command.Ids, SystemMessageContent.ForParticipantChange(participantChange), command.Now);
            }
        }
    }
}
