namespace Soga.Configuration;

/// <summary>
/// The hard limits that bound what <see cref="SogaOptions"/> can be configured to, and that the persistence model is
/// sized for.
/// </summary>
/// <remarks>
/// Keeping every limit here means the options validator, the storage mapping, and the tests all read the same numbers.
/// </remarks>
public static class SogaLimits
{
    /// <summary>The maximum supported normalized host user identifier length.</summary>
    public const int MaximumUserIdLength = 200;

    /// <summary>The maximum supported normalized tenant identifier length.</summary>
    public const int MaximumTenantIdLength = 100;

    /// <summary>The maximum supported client message identifier length.</summary>
    public const int MaximumClientMessageIdLength = 100;

    /// <summary>The maximum supported external reference length.</summary>
    public const int MaximumExternalReferenceLength = 200;

    /// <summary>The smallest allowed message text length setting.</summary>
    public const int MinimumTextLength = 1;

    /// <summary>The largest message text length the persistence model can store.</summary>
    public const int AbsoluteMaximumTextLength = 10_000;

    /// <summary>The smallest allowed group title length setting.</summary>
    public const int MinimumTitleLength = 1;

    /// <summary>The largest group title the persistence model can store.</summary>
    public const int AbsoluteMaximumTitleLength = 200;

    /// <summary>The smallest allowed conversation metadata size setting, in UTF-8 bytes (an empty JSON object).</summary>
    public const int MinimumMetadataBytes = 2;

    /// <summary>The largest conversation metadata document the persistence model can store, in UTF-8 bytes.</summary>
    public const int AbsoluteMaximumMetadataBytes = 16_384;

    /// <summary>The smallest allowed number of metadata properties.</summary>
    public const int MinimumMetadataProperties = 1;

    /// <summary>The largest allowed number of metadata properties.</summary>
    public const int AbsoluteMaximumMetadataProperties = 100;

    /// <summary>The smallest allowed group size.</summary>
    public const int MinimumGroupParticipants = 2;

    /// <summary>The largest allowed group size.</summary>
    public const int AbsoluteMaximumGroupParticipants = 10_000;

    /// <summary>The smallest allowed page or batch size.</summary>
    public const int MinimumPageSize = 1;

    /// <summary>The largest allowed page or batch size.</summary>
    public const int AbsoluteMaximumPageSize = 500;

    /// <summary>The smallest allowed request body limit, in bytes.</summary>
    public const int MinimumRequestBodyBytes = 1024;

    /// <summary>The largest allowed request body limit, in bytes.</summary>
    public const int AbsoluteMaximumRequestBodyBytes = 1_048_576;
}
