using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// The mutable state types held by <see cref="InMemorySogaStore"/>.
/// </summary>
internal sealed partial class InMemorySogaStore
{
    /// <summary>
    /// Represents the mutable state of one conversation.
    /// </summary>
    /// <param name="id">The conversation ID.</param>
    /// <param name="tenantId">The tenant that owns the conversation.</param>
    /// <param name="kind">The conversation kind.</param>
    /// <param name="title">The group title, if any.</param>
    /// <param name="externalReference">The host reference, if any.</param>
    /// <param name="metadataJson">The canonical metadata JSON, if any.</param>
    /// <param name="createdAt">When the conversation was created.</param>
    private sealed class ConversationState(
        Guid id,
        string tenantId,
        string kind,
        string? title,
        string? externalReference,
        string? metadataJson,
        DateTimeOffset createdAt)
    {
        /// <summary>Gets the conversation ID.</summary>
        public Guid Id { get; } = id;

        /// <summary>Gets the tenant that owns the conversation.</summary>
        public string TenantId { get; } = tenantId;

        /// <summary>Gets the conversation kind.</summary>
        public string Kind { get; } = kind;

        /// <summary>Gets or sets the group title.</summary>
        public string? Title { get; set; } = title;

        /// <summary>Gets the host reference.</summary>
        public string? ExternalReference { get; } = externalReference;

        /// <summary>Gets or sets the canonical metadata JSON.</summary>
        public string? MetadataJson { get; set; } = metadataJson;

        /// <summary>Gets when the conversation was created.</summary>
        public DateTimeOffset CreatedAt { get; } = createdAt;

        /// <summary>Gets or sets when the group was archived, or <see langword="null"/> if it is active.</summary>
        public DateTimeOffset? ArchivedAt { get; set; }

        /// <summary>Gets or sets when the last message was added.</summary>
        public DateTimeOffset LastActivityAt { get; set; } = createdAt;

        /// <summary>Gets or sets the sequence of the newest message.</summary>
        public long LastSequence { get; set; }

        /// <summary>Gets every current and former participant.</summary>
        public List<ParticipantState> Participants { get; } = [];

        /// <summary>Gets every message, including tombstones, in sequence order.</summary>
        public List<MessageRecord> Messages { get; } = [];
    }

    /// <summary>
    /// Represents the mutable state of one participant.
    /// </summary>
    /// <param name="userId">The participant's user ID.</param>
    /// <param name="role">The participant's role.</param>
    /// <param name="joinedAt">When the participant joined.</param>
    private sealed class ParticipantState(string userId, SogaRole role, DateTimeOffset joinedAt)
    {
        /// <summary>Gets the participant's user ID.</summary>
        public string UserId { get; } = userId;

        /// <summary>Gets or sets the participant's role.</summary>
        public SogaRole Role { get; set; } = role;

        /// <summary>Gets or sets when the participant last joined.</summary>
        public DateTimeOffset JoinedAt { get; set; } = joinedAt;

        /// <summary>Gets or sets when the participant left, or <see langword="null"/> if they are active.</summary>
        public DateTimeOffset? LeftAt { get; set; }

        /// <summary>Gets or sets the participant's read position.</summary>
        public long LastReadSequence { get; set; }
    }

    /// <summary>
    /// Represents one entry in a tenant's change log.
    /// </summary>
    /// <param name="Sequence">The entry's position in the tenant's change log.</param>
    /// <param name="EventId">The event ID, shared with the outbox entry.</param>
    /// <param name="ConversationId">The conversation the change concerns.</param>
    /// <param name="Payload">The serialized event.</param>
    /// <param name="TargetUserId">A user who must see the change even without access to the conversation.</param>
    private sealed record ChangeEntry(long Sequence, Guid EventId, Guid ConversationId, string Payload, string? TargetUserId);

    /// <summary>
    /// Represents a previous version of an edited message.
    /// </summary>
    /// <param name="MessageId">The ID of the message.</param>
    /// <param name="Version">The version number, starting at 1 for the original text.</param>
    /// <param name="PreviousContent">The text before the edit.</param>
    /// <param name="EditedAt">When the edit happened.</param>
    private sealed record EditAudit(Guid MessageId, int Version, string PreviousContent, DateTimeOffset EditedAt);

    /// <summary>
    /// Represents one outbox entry and its delivery state.
    /// </summary>
    /// <param name="id">The event ID.</param>
    /// <param name="tenantId">The tenant of the event.</param>
    /// <param name="eventType">The event type.</param>
    /// <param name="payload">The serialized event.</param>
    /// <param name="occurredAt">When the event happened.</param>
    private sealed class OutboxEntry(Guid id, string tenantId, string eventType, string payload, DateTimeOffset occurredAt)
    {
        /// <summary>Gets the event ID.</summary>
        public Guid Id { get; } = id;

        /// <summary>Gets the tenant of the event.</summary>
        public string TenantId { get; } = tenantId;

        /// <summary>Gets the event type.</summary>
        public string EventType { get; } = eventType;

        /// <summary>Gets the serialized event.</summary>
        public string Payload { get; } = payload;

        /// <summary>Gets when the event happened.</summary>
        public DateTimeOffset OccurredAt { get; } = occurredAt;

        /// <summary>Gets or sets the earliest time the event may next be claimed.</summary>
        public DateTimeOffset AvailableAt { get; set; } = occurredAt;

        /// <summary>Gets or sets when the current claim expires, or <see langword="null"/> if unclaimed.</summary>
        public DateTimeOffset? LeaseUntil { get; set; }

        /// <summary>Gets or sets how many times the event has been claimed.</summary>
        public int Attempts { get; set; }

        /// <summary>Gets or sets when the event was published, or <see langword="null"/> if it has not been.</summary>
        public DateTimeOffset? PublishedAt { get; set; }

        /// <summary>Gets or sets when the event failed for good, or <see langword="null"/> if it has not.</summary>
        public DateTimeOffset? FailedAt { get; set; }

        /// <summary>Gets or sets the exception type name of the last failure.</summary>
        public string? LastFailure { get; set; }
    }
}
