using System.Text;
using InativacaoUsuarioInter.Service.Arquivos;
using InativacaoUsuarioInter.Service.Entrada.Modelos;

namespace InativacaoUsuarioInter.Tests.Unit.Arquivos;

/// <summary>T032 — nomes e serialização das partes (research R-10, contracts/arquivos-entrada.md §7).</summary>
public sealed class PartesTests
{
    private const string Hash = "1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1c2d3e4f5a6b7c8d9e0f1a2b";

    private static RegistroEntrada Registro(string cpf, string id, string nome, string bruto) =>
        new(0, bruto, cpf, cpf, id, nome, "k", ValidacaoRegistro.Valido);

    [Fact]
    public void Nome_da_parte_usa_base_raiz_tipo_e_hash_curto()
    {
        Assert.Equal("20261111103000__sucesso-1a2b3c4d.json", NomeadorPartes.Parte("20261111103000", NomeadorPartes.Sucesso, Hash, ".json"));
        Assert.Equal("20261111103000__falhas-1a2b3c4d.txt", NomeadorPartes.Parte("20261111103000", NomeadorPartes.Falhas, Hash, ".txt"));
        Assert.Equal("20261111103000__pendente-1a2b3c4d.json", NomeadorPartes.Parte("20261111103000", NomeadorPartes.Pendente, Hash, ".json"));
    }

    [Fact]
    public void Colisao_gera_base_com_hash_curto() =>
        Assert.Equal("20261111103000__1a2b3c4d.json", NomeadorPartes.Colisao("20261111103000.json", Hash));

    [Fact]
    public void Json_eh_indentado_em_ordem_contratual_utf8_sem_bom_e_preserva_acentos()
    {
        var bytes = SerializadorPartes.SerializarRegistros(
            FormatoArquivo.Json, [Registro("22222222222", "1", "Conceição", "x")]);
        var texto = Encoding.UTF8.GetString(bytes);

        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Equal(
            "[\r\n  {\r\n    \"cpf\": \"22222222222\",\r\n    \"id_responsavel\": \"1\",\r\n    \"nome_responsavel\": \"Conceição\"\r\n  }\r\n]",
            texto);
    }

    [Fact]
    public void Txt_usa_crlf_e_cpf_normalizado()
    {
        var bytes = SerializadorPartes.SerializarRegistros(
            FormatoArquivo.Txt, [Registro("33333333333", "2", "Carlos, Jr.", "333.333.333-33,2,Carlos, Jr.")]);

        Assert.Equal("33333333333,2,Carlos, Jr.\r\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Parte_de_falhas_preserva_texto_bruto()
    {
        var txt = SerializadorPartes.SerializarBrutos(FormatoArquivo.Txt, [Registro("x", "1", "M", "abc,1,Maria")]);
        var json = SerializadorPartes.SerializarBrutos(
            FormatoArquivo.Json, [Registro("x", "1", "M", """{ "cpf": 22222222222, "id_responsavel": "1" }""")]);

        Assert.Equal("abc,1,Maria\r\n", Encoding.UTF8.GetString(txt));
        Assert.Contains("\"cpf\": 22222222222", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
    }
}
