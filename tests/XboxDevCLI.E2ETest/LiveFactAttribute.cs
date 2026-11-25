// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace XboxDevCLI.E2ETest;

/// <summary>
/// A test that runs the CLI against production as the signed-in account. It's skipped until every
/// <c>XBOXDEVCLI_E2E_</c> setting is set.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute() => Skip = E2ESettings.SkipReason;
}

/// <summary>
/// A test that runs the CLI against production as a signed-in personal Microsoft account. It's skipped until every
/// <see cref="MsaSettings"/> setting is set.
/// </summary>
public sealed class MsaFactAttribute : FactAttribute
{
    public MsaFactAttribute() => Skip = MsaSettings.SkipReason;
}
