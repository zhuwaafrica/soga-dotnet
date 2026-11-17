using Microsoft.Extensions.Options;
using Soga.Abstractions;
using Soga.Authorization;
using Soga.Configuration;
using Soga.Contracts;
using Soga.Conversations;
using Soga.Diagnostics;
using Soga.Errors;
using Soga.Extensibility;
using Soga.Identity;
using Soga.Persistence;
using Soga.Realtime;
using Soga.Synchronization;

namespace Soga.Messages;

/// <summary>
/// The default <see cref="ISogaSystemMessages"/>, which lets trusted host code post system messages.
/// </summary>
/// <param name="store">The persistence store.</param>
/// <param name="ids">The generator for message and event IDs.</param>
/// <param name="clock">The clock that timestamps messages.</param>
/// <param name="signal">Wakes the outbox dispatcher when a message is stored.</param>
/// <param name="options">The Soga options that hold the text-length limit.</param>
/// <remarks>
/// System messages are sent as <see cref="SogaSystemSender.UserId"/>. No HTTP endpoint or hub method reaches this
/// service, so participants can never create, edit, or delete a system message.
/// </remarks>
internal sealed class SogaSystemMessages(
    ISogaStore store,
    ISogaIdGenerator ids,
    TimeProvider clock,
    SogaOutboxSignal signal,
    IOptions<SogaOptions> options) : ISogaSystemMessages
{
    /// <inheritdoc />
    public async Task<MessageResponse?> SendAsync(
        string tenantId,
        Guid conversationId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > options.Value.Messages.MaximumTextLength)
        {
            throw new ArgumentException($"The text cannot exceed {options.Value.Messages.MaximumTextLength} characters.", nameof(text));
        }

        var messageId = ids.NewId();
        var outcome = await store.AppendMessageAsync(
            new AppendMessageCommand(
                tenantId.Trim(),
                SogaSystemSender.UserId,
                conversationId,
                $"sys:{messageId:N}",
                text,
                messageId,
                ids.NewId(),
                clock.GetUtcNow(),
                IsSystem: true),
            cancellationToken);

        if (outcome.Status != AppendStatus.Created)
        {
            return null;
        }

        signal.Notify();
        return outcome.Message!.ToResponse();
    }
}
