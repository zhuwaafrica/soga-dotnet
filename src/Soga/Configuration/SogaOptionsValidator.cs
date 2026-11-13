using Microsoft.Extensions.Options;

namespace Soga.Configuration;

/// <summary>
/// Validates <see cref="SogaOptions"/> when the application starts.
/// </summary>
/// <remarks>
/// Every rule is checked and every failure is reported together, so a misconfigured host fails fast with one complete
/// list of problems instead of one error per restart. Messages name the configuration key, for example
/// <c>Soga:Messages:MaximumTextLength</c>, so they can be fixed directly in <c>appsettings.json</c>.
/// </remarks>
internal sealed class SogaOptionsValidator : IValidateOptions<SogaOptions>
{
    /// <summary>
    /// Validates a <see cref="SogaOptions"/> instance.
    /// </summary>
    /// <param name="name">The name of the options instance being validated. Soga uses unnamed options.</param>
    /// <param name="options">The options to validate.</param>
    /// <returns>
    /// <see cref="ValidateOptionsResult.Success"/> when every rule passes; otherwise, a failed result listing every
    /// broken rule.
    /// </returns>
    public ValidateOptionsResult Validate(string? name, SogaOptions options)
    {
        var failures = new List<string>();

        Require(failures, IsValidRoutePrefix(options.RoutePrefix),
            $"{Key(nameof(SogaOptions.RoutePrefix))} must start with '/' and must not end with '/'.");
        Require(failures,
            !string.IsNullOrWhiteSpace(options.DefaultTenantId) &&
            options.DefaultTenantId.Trim().Length <= SogaLimits.MaximumTenantIdLength,
            $"{Key(nameof(SogaOptions.DefaultTenantId))} is required and cannot exceed {SogaLimits.MaximumTenantIdLength} characters.");
        Require(failures, !string.IsNullOrWhiteSpace(options.Identity.UserIdClaimType),
            $"{Key(nameof(SogaOptions.Identity), nameof(SogaIdentityOptions.UserIdClaimType))} is required.");

        ValidatePaging(nameof(SogaOptions.Conversations), options.Conversations.DefaultPageSize,
            options.Conversations.MaximumPageSize, failures);
        ValidatePaging(nameof(SogaOptions.Messages), options.Messages.DefaultPageSize, options.Messages.MaximumPageSize,
            failures);
        ValidatePaging(nameof(SogaOptions.Sync), options.Sync.DefaultBatchSize, options.Sync.MaximumBatchSize, failures,
            nameof(SogaSyncOptions.DefaultBatchSize), nameof(SogaSyncOptions.MaximumBatchSize));

        RequireRange(failures, Key(nameof(SogaOptions.Messages), nameof(SogaMessageOptions.MaximumTextLength)),
            options.Messages.MaximumTextLength, SogaLimits.MinimumTextLength, SogaLimits.AbsoluteMaximumTextLength);
        Require(failures, options.Messages.EditWindow is not { } window || window > TimeSpan.Zero,
            $"{Key(nameof(SogaOptions.Messages), nameof(SogaMessageOptions.EditWindow))} must be greater than zero, or unset for no limit.");
        RequireRange(failures,
            Key(nameof(SogaOptions.Conversations), nameof(SogaConversationOptions.MaximumMetadataBytes)),
            options.Conversations.MaximumMetadataBytes, SogaLimits.MinimumMetadataBytes,
            SogaLimits.AbsoluteMaximumMetadataBytes);
        RequireRange(failures,
            Key(nameof(SogaOptions.Conversations), nameof(SogaConversationOptions.MaximumMetadataProperties)),
            options.Conversations.MaximumMetadataProperties, SogaLimits.MinimumMetadataProperties,
            SogaLimits.AbsoluteMaximumMetadataProperties);
        RequireRange(failures, Key(nameof(SogaOptions.Groups), nameof(SogaGroupOptions.MaximumParticipants)),
            options.Groups.MaximumParticipants, SogaLimits.MinimumGroupParticipants,
            SogaLimits.AbsoluteMaximumGroupParticipants);
        RequireRange(failures, Key(nameof(SogaOptions.Groups), nameof(SogaGroupOptions.MaximumTitleLength)),
            options.Groups.MaximumTitleLength, SogaLimits.MinimumTitleLength, SogaLimits.AbsoluteMaximumTitleLength);

        var typing = options.Typing;
        Require(failures,
            typing.Timeout > TimeSpan.Zero &&
            typing.MinimumBroadcastInterval >= TimeSpan.Zero &&
            typing.MinimumBroadcastInterval < typing.Timeout,
            $"{Key(nameof(SogaOptions.Typing), nameof(SogaTypingOptions.Timeout))} must be positive and greater than MinimumBroadcastInterval.");
        Require(failures, options.Presence.OfflineGracePeriod >= TimeSpan.Zero,
            $"{Key(nameof(SogaOptions.Presence), nameof(SogaPresenceOptions.OfflineGracePeriod))} cannot be negative.");

        var outbox = options.Outbox;
        Require(failures, outbox.PollInterval > TimeSpan.Zero,
            $"{Key(nameof(SogaOptions.Outbox), nameof(SogaOutboxOptions.PollInterval))} must be greater than zero.");
        RequireRange(failures, Key(nameof(SogaOptions.Outbox), nameof(SogaOutboxOptions.BatchSize)), outbox.BatchSize,
            SogaLimits.MinimumPageSize, SogaLimits.AbsoluteMaximumPageSize);
        Require(failures, outbox.LeaseDuration > TimeSpan.Zero,
            $"{Key(nameof(SogaOptions.Outbox), nameof(SogaOutboxOptions.LeaseDuration))} must be greater than zero.");
        Require(failures, outbox.MaximumAttempts >= 1,
            $"{Key(nameof(SogaOptions.Outbox), nameof(SogaOutboxOptions.MaximumAttempts))} must be at least 1.");
        Require(failures, outbox.BaseRetryDelay > TimeSpan.Zero && outbox.MaximumRetryDelay >= outbox.BaseRetryDelay,
            $"{Key(nameof(SogaOptions.Outbox), nameof(SogaOutboxOptions.BaseRetryDelay))} must be positive and not exceed MaximumRetryDelay.");
        Require(failures, outbox.Retention > TimeSpan.Zero,
            $"{Key(nameof(SogaOptions.Outbox), nameof(SogaOutboxOptions.Retention))} must be greater than zero.");

        RequireRange(failures, Key(nameof(SogaOptions.Security), nameof(SogaSecurityOptions.MaximumRequestBodyBytes)),
            options.Security.MaximumRequestBodyBytes, SogaLimits.MinimumRequestBodyBytes,
            SogaLimits.AbsoluteMaximumRequestBodyBytes);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// Checks that a default page size is at least 1, does not exceed its maximum, and that the maximum is at most 500.
    /// </summary>
    /// <param name="section">The name of the options section being validated, such as <c>Messages</c>.</param>
    /// <param name="defaultSize">The configured default page size.</param>
    /// <param name="maximumSize">The configured maximum page size.</param>
    /// <param name="failures">The list that receives a failure message when the rule is broken.</param>
    /// <param name="defaultName">The name of the default-size setting, used in the failure message.</param>
    /// <param name="maximumName">The name of the maximum-size setting, used in the failure message.</param>
    private static void ValidatePaging(
        string section,
        int defaultSize,
        int maximumSize,
        List<string> failures,
        string defaultName = nameof(SogaConversationOptions.DefaultPageSize),
        string maximumName = nameof(SogaConversationOptions.MaximumPageSize))
    {
        if (defaultSize < SogaLimits.MinimumPageSize || maximumSize < defaultSize ||
            maximumSize > SogaLimits.AbsoluteMaximumPageSize)
        {
            var path = Key(section);
            failures.Add(
                $"{path}:{defaultName} must be at least 1 and not exceed {path}:{maximumName}, which cannot exceed {SogaLimits.AbsoluteMaximumPageSize}.");
        }
    }

    /// <summary>Builds the configuration key for a root setting, such as <c>Soga:RoutePrefix</c>.</summary>
    private static string Key(string name) => $"{SogaOptions.SectionName}:{name}";

    /// <summary>Builds the configuration key for a nested setting, such as <c>Soga:Messages:EditWindow</c>.</summary>
    private static string Key(string section, string property) => $"{Key(section)}:{property}";

    private static bool IsValidRoutePrefix(string? prefix) =>
        !string.IsNullOrWhiteSpace(prefix) && prefix.StartsWith('/') && !prefix.EndsWith('/');

    /// <summary>Records <paramref name="message"/> when <paramref name="isValid"/> is false.</summary>
    private static void Require(List<string> failures, bool isValid, string message)
    {
        if (!isValid)
        {
            failures.Add(message);
        }
    }

    /// <summary>Records a "must be between" failure for <paramref name="key"/> when the value is outside the inclusive range.</summary>
    private static void RequireRange(List<string> failures, string key, long value, long minimum, long maximum) =>
        Require(failures, value >= minimum && value <= maximum, $"{key} must be between {minimum} and {maximum}.");
}
