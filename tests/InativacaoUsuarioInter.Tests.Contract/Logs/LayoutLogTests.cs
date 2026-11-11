using System.Text;
using InativacaoUsuarioInter.Service.Entrada.Modelos;
using InativacaoUsuarioInter.Service.Estado;
using InativacaoUsuarioInter.Service.Registros;
using InativacaoUsuarioInter.Service.Resiliencia;
using InativacaoUsuarioInter.Tests.Comum;

namespace InativacaoUsuarioInter.Tests.Contract.Logs;

/// <summary>T028 — layout contratual dos logs (contracts/logs-auditoria-falhas.md, golden files).</summary>
public sealed class LayoutLogTests : IDisposable
{
    private static readonly DateTimeOffset Instante = new(2026, 11, 11, 10, 30, 0, TimeSpan.FromHours(-3));
    private readonly string _pasta = Path.Combine(Path.GetTempPath(), "contrato-logs-" + Guid.NewGuid().ToString("N"));

    private static string[] Golden(string nome) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Logs", "golden", nome), Encoding.UTF8);

    [Fact]
    public void Linha_de_auditoria_igual_ao_golden()
    {
        var linha = FormatadorLinhaLog.FormatarAuditoria(
            Instante, Instancia.Inter, "20261111102958.json", "22222222222", "1", "Maria Eduarda", FormatadorLinhaLog.StatusInativado);

        Assert.Equal(Golden("auditoria.log")[0], linha);
    }

    [Fact]
    public void Linhas_de_falha_iguais_ao_golden()
    {
        var instante = Instante.AddSeconds(1);
        var golden = Golden("falhas.log");

        Assert.Equal(golden[0], FormatadorLinhaLog.FormatarFalha(instante, Instancia.Inter, "20261111102958.json", "44444444444", CausasFalha.CpfNaoEncontrado));
        Assert.Equal(golden[1], FormatadorLinhaLog.FormatarFalha(instante, Instancia.Inter, "20261111103500.txt", null, CausasFalha.ArquivoInvalido));
    }

    [Fact]
    public void Status_ja_inativo_e_ambiente_condominio()
    {
        var linha = FormatadorLinhaLog.FormatarAuditoria(
            Instante, Instancia.Condominio, "x.txt", "01234567890", "7", "Ana", FormatadorLinhaLog.StatusJaInativo);

        Assert.Equal("2026-11-11T10:30:00-03:00 | Condominio | arquivo=x.txt | cpf=01234567890 | id_responsavel=7 | nome_responsavel=Ana | status=JA_INATIVO", linha);
    }

    [Fact]
    public void Barra_vertical_e_quebras_de_linha_em_valores_viram_espaco()
    {
        var linha = FormatadorLinhaLog.FormatarAuditoria(
            Instante, Instancia.Inter, "a|b.json", "22222222222", "1\r\n", "Maria | status=INATIVADO\nfalso", FormatadorLinhaLog.StatusInativado);

        Assert.DoesNotContain('\n', linha);
        Assert.DoesNotContain('\r', linha);
        Assert.Equal(7, linha.Split(" | ").Length);
        Assert.EndsWith("| status=INATIVADO", linha, StringComparison.Ordinal);
        Assert.Contains("nome_responsavel=Maria   status=INATIVADO falso", linha, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Arquivo_gravado_eh_utf8_sem_bom_com_crlf_e_somente_acrescimo()
    {
        var configuracao = ConfiguracaoTeste.Criar(Path.Combine(_pasta, "estado"), _pasta);
        var escritor = new EscritorLogAppendOnly(configuracao, new ProvedorPipelines(configuracao));
        var linha1 = FormatadorLinhaLog.FormatarAuditoria(Instante, Instancia.Inter, "a.json", "22222222222", "1", "Conceição", "INATIVADO");
        var linha2 = FormatadorLinhaLog.FormatarFalha(Instante, Instancia.Inter, "a.json", "33333333333", CausasFalha.CpfNaoEncontrado);

        await escritor.AnexarAsync(TipoLog.Auditoria, linha1, null, TestContext.Current.CancellationToken);
        await escritor.AnexarAsync(TipoLog.Auditoria, linha2, null, TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(escritor.Caminho(TipoLog.Auditoria), TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_pasta, "Auditoria", "log_inativacao.log"), escritor.Caminho(TipoLog.Auditoria));
        Assert.Equal(Path.Combine(_pasta, "Falhas", "log_inativacao.log"), escritor.Caminho(TipoLog.Falhas));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal(linha1 + "\r\n" + linha2 + "\r\n", new UTF8Encoding(false).GetString(bytes));
        Assert.True(await escritor.ContemLinhaAsync(TipoLog.Auditoria, linha2, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_pasta))
        {
            Directory.Delete(_pasta, recursive: true);
        }
    }
}
