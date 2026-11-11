using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Inativacao;
using InativacaoUsuarioInter.Service.Resiliencia;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace InativacaoUsuarioInter.Tests.Unit.Resiliencia;

/// <summary>T011 — classificação de erros (research R-05): infraestrutura é sempre transitória.</summary>
public sealed class ClassificadorErrosTests
{
    [Theory]
    [InlineData(-2, CategoriaErro.Timeout)]
    [InlineData(53, CategoriaErro.Conexao)]
    [InlineData(233, CategoriaErro.Conexao)]
    [InlineData(4060, CategoriaErro.Conexao)]
    [InlineData(10053, CategoriaErro.Conexao)]
    [InlineData(10054, CategoriaErro.Conexao)]
    [InlineData(10060, CategoriaErro.Conexao)]
    [InlineData(11001, CategoriaErro.Conexao)]
    [InlineData(40613, CategoriaErro.Conexao)]
    [InlineData(1205, CategoriaErro.Bloqueio)]
    [InlineData(18456, CategoriaErro.Configuracao)]
    [InlineData(208, CategoriaErro.Configuracao)]
    [InlineData(229, CategoriaErro.Configuracao)]
    [InlineData(230, CategoriaErro.Configuracao)]
    public void Numero_sql_de_infraestrutura_eh_transitorio(int numero, CategoriaErro categoria)
    {
        var classificado = ClassificadorErrosSql.ClassificarNumero(numero);

        Assert.Equal(ClasseErro.Transitoria, classificado.Classe);
        Assert.Equal(categoria, classificado.Categoria);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), CategoriaErro.Bloqueio)]
    [InlineData(unchecked((int)0x80070021), CategoriaErro.Bloqueio)]
    [InlineData(unchecked((int)0x80070035), CategoriaErro.Conexao)]
    [InlineData(unchecked((int)0x80070040), CategoriaErro.Conexao)]
    [InlineData(unchecked((int)0x80070043), CategoriaErro.Conexao)]
    public void IOException_por_hresult_eh_transitoria(int hresult, CategoriaErro categoria)
    {
        var classificado = ClassificadorErros.Classificar(new IOException("falha", hresult));

        Assert.Equal(ClasseErro.Transitoria, classificado.Classe);
        Assert.Equal(categoria, classificado.Categoria);
    }

    [Fact]
    public void Acesso_negado_eh_transitorio_de_permissao()
    {
        var classificado = ClassificadorErros.Classificar(new UnauthorizedAccessException());

        Assert.Equal(new ErroClassificado(ClasseErro.Transitoria, CategoriaErro.Permissao), classificado);
    }

    [Fact]
    public void Timeouts_e_circuito_aberto_sao_transitorios()
    {
        Assert.Equal(ClasseErro.Transitoria, ClassificadorErros.Classificar(new TimeoutException()).Classe);
        Assert.Equal(ClasseErro.Transitoria, ClassificadorErros.Classificar(new TimeoutRejectedException()).Classe);
        Assert.Equal(ClasseErro.Transitoria, ClassificadorErros.Classificar(new BrokenCircuitException()).Classe);
    }

    [Fact]
    public void Excecao_desconhecida_nunca_vira_definitiva()
    {
        var classificado = ClassificadorErros.Classificar(new InvalidOperationException("inesperado"));

        Assert.Equal(ClasseErro.Transitoria, classificado.Classe);
        Assert.Equal(CategoriaErro.Desconhecida, classificado.Categoria);
    }

    [Fact]
    public void Interrupcao_simulada_e_cancelamento_do_host_nao_sao_capturaveis()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.False(ClassificadorErros.Capturavel(new InterrupcaoSimuladaException(), CancellationToken.None));
        Assert.False(ClassificadorErros.Capturavel(new OperationCanceledException(), cts.Token));
        Assert.True(ClassificadorErros.Capturavel(new IOException(), CancellationToken.None));
    }

    [Fact]
    public void Classificador_IO_nao_reconhece_excecao_sem_relacao()
    {
        Assert.Null(ClassificadorErrosIO.Classificar(new FormatException()));
    }
}
