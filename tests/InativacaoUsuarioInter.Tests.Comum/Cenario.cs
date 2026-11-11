using InativacaoUsuarioInter.Service.Ciclo;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace InativacaoUsuarioInter.Tests.Comum;

/// <summary>
/// Cenário de integração: banco isolado com a réplica de CHMain + diretórios temporários + serviços montados
/// pela raiz de composição real. <see cref="ReiniciarAsync"/> simula o reinício do serviço (novo container DI
/// sobre o mesmo diretório de estado e as mesmas pastas).
/// </summary>
public sealed class Cenario : IAsyncDisposable
{
    private readonly Action<IServiceCollection>? _ajustarServicos;

    private Cenario(BancoTeste banco, AmbienteTeste ambiente, Action<IServiceCollection>? ajustarServicos)
    {
        Banco = banco;
        Ambiente = ambiente;
        _ajustarServicos = ajustarServicos;
        Servicos = ambiente.CriarServicos(ajustarServicos);
    }

    public BancoTeste Banco { get; }

    public AmbienteTeste Ambiente { get; }

    public ServiceProvider Servicos { get; private set; }

    public static async Task<Cenario> CriarAsync(
        SqlServerFixture sql,
        string instancia = "Inter",
        Action<AmbienteTeste>? ajustarAmbiente = null,
        Action<IServiceCollection>? ajustarServicos = null,
        Func<BancoTeste, string>? conexao = null,
        CancellationToken cancelamento = default)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var banco = await sql.CriarBancoAsync(cancelamento);
        var ambiente = AmbienteTeste.Criar(conexao?.Invoke(banco) ?? banco.StringConexao, instancia);
        ajustarAmbiente?.Invoke(ambiente);
        return new Cenario(banco, ambiente, ajustarServicos);
    }

    public T Obter<T>()
        where T : notnull => Servicos.GetRequiredService<T>();

    public Task<ResumoCiclo> CicloAsync(CancellationToken cancelamento = default) =>
        Obter<IOrquestradorCiclo>().ExecutarCicloAsync(cancelamento);

    /// <summary>Descarta os serviços e cria novos sobre o mesmo estado (reinício do processo).</summary>
    public async Task ReiniciarAsync(Action<IServiceCollection>? ajustarServicos = null)
    {
        await Servicos.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Servicos = Ambiente.CriarServicos(ajustarServicos ?? _ajustarServicos);
    }

    public async ValueTask DisposeAsync()
    {
        await Servicos.DisposeAsync();
        Ambiente.Dispose();
    }
}
