using InativacaoUsuarioInter.Tests.Comum;

// Um único SQL Server em container para o assembly; testes em série porque a telemetria
// (ActivitySource/Meter estáticos) e o proxy de falhas são compartilhados no processo.
[assembly: AssemblyFixture(typeof(SqlServerFixture))]
