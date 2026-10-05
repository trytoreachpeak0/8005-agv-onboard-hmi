using System.Runtime.CompilerServices;
using SQCD.Agv.Infrastructure;

[assembly: AssemblyFixture(typeof(SQCD.Agv.UnitTests.SqliteProviderAssemblyFixture))]

namespace SQCD.Agv.UnitTests;

/// <summary>
/// Sets the SQLite provider before any test in this assembly runs (8005-agv-onboard-hmi#261).
/// </summary>
/// <remarks>
/// <para>
/// The provider is set in one place only, the static constructor of <see cref="SqliteWireToGateJournal"/>. A test that
/// opens a bare <c>SqliteConnection</c> before it constructs a journal -- one that writes a journal file in an older
/// shape and then hands it to the journal -- passed or failed by the order the tests happened to run in: first in the
/// process, it threw <c>You need to call SQLitePCL.raw.SetProvider()</c>.
/// </para>
/// <para>
/// This runs that same static constructor up front instead of naming a provider of its own, so which provider the
/// tests use is still decided where the product decides it.
/// </para>
/// </remarks>
public sealed class SqliteProviderAssemblyFixture
{
    public SqliteProviderAssemblyFixture() =>
        RuntimeHelpers.RunClassConstructor(typeof(SqliteWireToGateJournal).TypeHandle);
}
