using Soga.Contracts;
using Soga.Conversations;
using Soga.Groups;
using Soga.Messages;
using Soga.Synchronization;

namespace Soga.Persistence.InMemory;

/// <summary>
/// The transactional outbox for <see cref="InMemorySogaStore"/>.
/// </summary>
internal sealed partial class InMemorySogaStore
{
    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxMessage>> ClaimOutboxAsync(
        DateTimeOffset now,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var due = _outbox
                .Where(o => o.PublishedAt is null && o.FailedAt is null && o.AvailableAt <= now &&
                    (o.LeaseUntil is null || o.LeaseUntil <= now))
                // OrderBy is stable and the list is in commit order, so events with the same timestamp keep their commit order.
                .OrderBy(o => o.OccurredAt)
                .Take(batchSize)
                .ToList();

            foreach (var entry in due)
            {
                entry.LeaseUntil = now + leaseDuration;
                entry.Attempts++;
            }

            IReadOnlyList<OutboxMessage> claimed = due
                .Select(o => new OutboxMessage(o.Id, o.TenantId, o.EventType, o.Payload, o.Attempts))
                .ToList();
            return Task.FromResult(claimed);
        }
    }

    /// <inheritdoc />
    public Task CompleteOutboxAsync(OutboxMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = _outbox.FirstOrDefault(o => o.Id == message.Id && o.TenantId == message.TenantId);
            if (entry is not null)
            {
                entry.PublishedAt = now;
                entry.LeaseUntil = null;
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task FailOutboxAsync(
        OutboxMessage message,
        DateTimeOffset? retryAt,
        DateTimeOffset now,
        string failureCode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var entry = _outbox.FirstOrDefault(o => o.Id == message.Id && o.TenantId == message.TenantId);
            if (entry is not null)
            {
                entry.LeaseUntil = null;
                entry.LastFailure = failureCode;
                if (retryAt is null)
                {
                    entry.FailedAt = now;
                }
                else
                {
                    entry.AvailableAt = retryAt.Value;
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<int> PurgeOutboxAsync(DateTimeOffset publishedBefore, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return Task.FromResult(_outbox.RemoveAll(o => o.PublishedAt < publishedBefore));
        }
    }

    /// <inheritdoc />
    public Task<OutboxStats> GetOutboxStatsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var pending = _outbox.Where(o => o.PublishedAt is null && o.FailedAt is null).ToList();
            TimeSpan? oldest = pending.Count == 0 ? null : now - pending.Min(o => o.OccurredAt);
            return Task.FromResult(new OutboxStats(pending.Count, oldest, _outbox.Count(o => o.FailedAt is not null)));
        }
    }
}
