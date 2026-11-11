using System.Text;
using InativacaoUsuarioInter.Service.Entrada;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Registros;
using InativacaoUsuarioInter.Tests.Comum;
using Microsoft.Data.Sqlite;

namespace InativacaoUsuarioInter.Tests.Contract.ArquivosEntrada;

/// <summary>T027 — contrato dos arquivos de entrada (contracts/arquivos-entrada.md §3–§6).</summary>
public sealed class FormatoEntradaTests : IDisposable
{
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "contrato-entrada-" + Guid.NewGuid().ToString("N"));
    private readonly LeitorArquivo _leitor;

    public FormatoEntradaTests()
    {
        Directory.CreateDirectory(_pasta);
        var configuracao = ConfiguracaoTeste.Criar(Path.Combine(_pasta, "estado"), _pasta);
        _leitor = new LeitorArquivo(new ArmazenamentoEstadoSqlite(configuracao), configuracao);
    }

    private async Task<ResultadoLeitura> LerAsync(string nome, byte[] conteudo)
    {
        var caminho = Path.Combine(_pasta, nome);
        await File.WriteAllBytesAsync(caminho, conteudo, TestContext.Current.CancellationToken);
        return await _leitor.LerAsync(caminho, TestContext.Current.CancellationToken);
    }

    private Task<ResultadoLeitura> LerAsync(string nome, string conteudo, bool bom = false) =>
        LerAsync(nome, (bom ? [0xEF, 0xBB, 0xBF] : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(conteudo)).ToArray());

    [Fact]
    public async Task Exemplo_json_do_contrato_com_cpf_pontuado()
    {
        var leitura = await LerAsync("20261111103000.json", """
            [
              { "cpf": "22222222222", "id_responsavel": "1", "nome_responsavel": "Maria Eduarda" },
              { "cpf": "333.333.333-33", "id_responsavel": "2", "nome_responsavel": "Carlos Renato da Silva" }
            ]
            """, bom: true);

        Assert.Null(leitura.CausaRejeicao);
        Assert.Equal(["22222222222", "33333333333"], leitura.Arquivo!.Registros.Select(r => r.Cpf));
        Assert.All(leitura.Arquivo.Registros, r => Assert.Equal(ValidacaoRegistro.Valido, r.Validacao));
    }

    [Fact]
    public async Task Exemplo_txt_do_contrato_com_virgula_no_nome()
    {
        var leitura = await LerAsync("20261111103000.txt", "22222222222,1,Maria Eduarda\r\n333.333.333-33,2,Carlos Renato da Silva, Jr.\r\n");

        var registros = leitura.Arquivo!.Registros;
        Assert.Equal("Carlos Renato da Silva, Jr.", registros[1].NomeResponsavel);
        Assert.Equal("33333333333", registros[1].Cpf);
    }

    [Theory]
    [InlineData("01234567890", "01234567890")]
    [InlineData("012.345.678-90", "01234567890")]
    [InlineData(" 012 345 678 90 ", "01234567890")]
    [InlineData("0123456789", null)]
    [InlineData("012/345/678-90", null)]
    [InlineData("cpf", null)]
    [InlineData("1'; DROP TABLE CHMain;--", null)]
    public void Tabela_de_normalizacao_do_contrato(string entrada, string? esperado) =>
        Assert.Equal(esperado, NormalizadorCpf.Normalizar(entrada));

    [Fact]
    public async Task Cpf_numerico_em_json_eh_dado_invalido()
    {
        var leitura = await LerAsync("a.json", """[ { "cpf": 22222222222, "id_responsavel": "1", "nome_responsavel": "Maria" } ]""");

        Assert.Equal(ValidacaoRegistro.DadoInvalido, Assert.Single(leitura.Arquivo!.Registros).Validacao);
    }

    [Fact]
    public async Task Utf8_invalido_rejeita_o_arquivo() =>
        Assert.Equal(CausasFalha.ArquivoInvalido, (await LerAsync("a.txt", [0x32, 0x2C, 0x31, 0x2C, 0xC3, 0x28])).CausaRejeicao);

    [Theory]
    [InlineData("a.json", "[ { \"cpf\": ")]
    [InlineData("a.json", "{ \"cpf\": \"22222222222\" }")]
    [InlineData("a.json", "[]")]
    [InlineData("a.txt", "")]
    [InlineData("a.txt", "\r\n   \r\n")]
    public async Task Arquivo_malformado_ou_vazio_eh_arquivo_invalido(string nome, string conteudo) =>
        Assert.Equal(CausasFalha.ArquivoInvalido, (await LerAsync(nome, conteudo)).CausaRejeicao);

    [Fact]
    public async Task Mais_de_1000_registros_excede_o_limite()
    {
        var conteudo = string.Concat(Enumerable.Range(0, 1001).Select(i => $"{i:D11},1,Maria\r\n"));

        Assert.Equal(CausasFalha.ArquivoExcedeLimite, (await LerAsync("a.txt", conteudo)).CausaRejeicao);
    }

    [Fact]
    public async Task Exatamente_1000_registros_eh_aceito()
    {
        var conteudo = string.Concat(Enumerable.Range(0, 1000).Select(i => $"{i:D11},1,Maria\r\n"));

        Assert.Null((await LerAsync("a.txt", conteudo)).CausaRejeicao);
    }

    [Fact]
    public async Task Mais_de_1_MB_excede_o_limite()
    {
        var conteudo = "22222222222,1," + new string('x', 1_048_577);

        Assert.Equal(CausasFalha.ArquivoExcedeLimite, (await LerAsync("a.txt", conteudo)).CausaRejeicao);
    }

    [Theory]
    [InlineData("a.JSON")]
    [InlineData("a.Txt")]
    public void Extensao_eh_reconhecida_sem_diferenciar_maiusculas(string nome) =>
        Assert.NotNull(LeitorArquivo.FormatoPorExtensao(nome));

    [Theory]
    [InlineData("a.csv")]
    [InlineData("a.json.tmp")]
    [InlineData("a.json.traceparent")]
    [InlineData("a.part")]
    public void Outras_extensoes_sao_ignoradas(string nome) =>
        Assert.Null(LeitorArquivo.FormatoPorExtensao(nome));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_pasta, recursive: true);
    }
}
