// Copyright 2026, graphiti-net contributors
// Licensed under the Apache License, Version 2.0. See LICENSE.

using Microsoft.Extensions.Logging;

namespace GraphitiNet.Maintenance;

/// <summary>Log messages of the entity pipeline. Texts follow upstream's log lines.</summary>
internal static partial class MaintenanceLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "attribute_length_cap_skipped_required prompt={PromptName} group_id={GroupId} entity_uuid={EntityUuid} field={Field} reason={Reason} length={Length} cap={Cap}")]
    public static partial void AttributeCapSkippedRequired(
        ILogger logger, string promptName, string groupId, string entityUuid, string field, string reason, int length, int cap);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "attribute_length_cap_exceeded prompt={PromptName} group_id={GroupId} entity_uuid={EntityUuid} field={Field} reason={Reason} length={Length} cap={Cap}")]
    public static partial void AttributeCapExceeded(
        ILogger logger, string promptName, string groupId, string entityUuid, string field, string reason, int length, int cap);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Ignoring invalid {Variable}={Value}; expected a positive integer. Using default={Default}.")]
    public static partial void InvalidAttributeMaxLength(ILogger logger, string variable, string value, int @default);

    [LoggerMessage(Level = LogLevel.Debug, Message = "No entity types available for extraction after exclusions")]
    public static partial void NoEntityTypesAfterExclusions(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Extracted {Count} entities in {Milliseconds:F0} ms")]
    public static partial void ExtractedEntities(ILogger logger, int count, double milliseconds);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Identity-property node candidate lookup failed; falling back (node_uuid={NodeUuid}, group_id={GroupId}, identity_type={IdentityType})")]
    public static partial void IdentityLookupFailed(
        ILogger logger, Exception exception, string nodeUuid, string groupId, string identityType);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Exact-name node candidate lookup failed; falling back to semantic search (extracted_node_count={Count}, group_count={GroupCount})")]
    public static partial void ExactNameLookupFailed(ILogger logger, Exception exception, int count, int groupCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LLM did not return resolutions for IDs: {Ids}")]
    public static partial void MissingResolutions(ILogger logger, string ids);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "LLM returned invalid IDs outside valid range 0-{Max}: {ExtraIds} (all returned IDs: {AllIds})")]
    public static partial void ExtraResolutions(ILogger logger, int max, string extraIds, string allIds);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Skipping invalid LLM dedupe id {Id} (valid range: 0-{Max}, received {Count} resolutions)")]
    public static partial void InvalidDedupeId(ILogger logger, int id, int max, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Duplicate LLM dedupe id {Id} received; ignoring.")]
    public static partial void DuplicateDedupeId(ILogger logger, int id);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Invalid duplicate_candidate_id {CandidateId} for extracted node {NodeUuid}; treating as no duplicate.")]
    public static partial void InvalidDuplicateCandidateId(ILogger logger, int candidateId, string nodeUuid);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "duplicate_candidate_id {CandidateId} for extracted node {NodeUuid} is in another group; treating as no duplicate.")]
    public static partial void CrossGroupDuplicate(ILogger logger, int candidateId, string nodeUuid);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "LLM returned summary for unknown entity (first 30 chars): {Name}")]
    public static partial void UnknownSummaryEntity(ILogger logger, string name);
}
