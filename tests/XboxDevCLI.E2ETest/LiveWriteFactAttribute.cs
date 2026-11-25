// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Xunit;

namespace XboxDevCLI.E2ETest;

/// <summary>
/// A live test that writes to production and leaves nothing behind. It's skipped unless
/// <c>XBOXDEVCLI_E2E_WRITE</c> is <c>true</c>, as well as whenever a <see cref="LiveFactAttribute"/> test is.
/// </summary>
public sealed class LiveWriteFactAttribute : FactAttribute
{
    public LiveWriteFactAttribute() => Skip = E2ESettings.WriteSkipReason;
}
