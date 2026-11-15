using System.Security.Cryptography;
using System.Text;
using Xunit.Abstractions;

namespace Dante.Tests;

// Fase 0 da #147: o estado do Brain das retomadas medidas em 15/11 é restaurado das evidências e o pacote é reconstruído.
// No commit 97bd98d (formato pré-#289) o pacote precisa ser byte a byte o histórico; no formato vigente, os bytes
// precisam ser os pré-registrados abaixo e iguais entre duas restaurações independentes.
public sealed class RestauracaoDoBrain147Tests(ITestOutputHelper output)
{
    // Execuções medidas em 15/11/2026 e hash do manifesto publicado na #147. Outra execução não é aceita.
    internal static readonly Dictionary<string, string> Historicas = new(StringComparer.Ordinal)
    {
        ["20261115T151709Z-retomada-r1-f61fb9147a7d"] = "7d83c7cf4a0095f5d18fe3f734c92aaf176d30cece47dae75d3fe97b7561d9da",
        ["20261115T151449Z-retomada-r2-601a68d9783c"] = "d089a2ab926d58c7c0918b89756843e95d18c8ce878095a6f96c622823278d0c",
    };

    // SHA-256 do texto do pacote, da entrada B e do pacote.json (Id do pacote normalizado para o histórico) no formato vigente.
    // Formato C1 da #147 (cabeçalho enxuto e título em chave própria): r1 4.172 B, r2 4.109 B. Os pacotes compactos da #289,
    // medidos no lote 20261115T190731Z, eram r1 b7a6562f… (4.238 B) e r2 48ece640… (4.175 B).
    internal static readonly Dictionary<string, (string Texto, string EntradaB, string PacoteJson)> CompactoPreRegistrado = new(StringComparer.Ordinal)
    {
        ["20261115T151709Z-retomada-r1-f61fb9147a7d"] = ("43888267b0b2267bf33457676c7f44fc5f7bd63f37dafbdcd783544b61f9ad24",
            "ef6e764700eb4630ef3ab7ff52e4ff35867fc2c7503f4f7d001acb808965d0cc", "27fe1225f6d243535b67f9575b9631c3cf9c2f4d8b7369c08f0f0dde238560e0"),
        ["20261115T151449Z-retomada-r2-601a68d9783c"] = ("eee0769fba4a3849e65df5651538fcd261fcba038ca972f6eedd649e4859997f",
            "c12c08c74dddfab0965b7cb0eab9d219bef806c738ec209aafa919bce22f4a53", "847b3fd20438a7efb7f1b9011a3060759f896d64bc8f2ef884641bec86a2c90c"),
    };

    internal static string Diretorio(string id) => Path.Combine(RaizDeEvidencias147.Padrao(), "execucoes", id);

    [EvidenciaHistorica147Theory]
    [InlineData("20261115T151709Z-retomada-r1-f61fb9147a7d")]
    [InlineData("20261115T151449Z-retomada-r2-601a68d9783c")]
    public async Task EstadoRestauradoReproduzOPacoteByteAByte(string id)
    {
        var diretorio = Diretorio(id);
        Assert.Equal(Historicas[id], Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(diretorio, "manifesto.json")))));
        var evidencia = EvidenciaDeRetomada147.Ler(diretorio);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        // Duas restaurações em bancos descartáveis distintos: a reprodução não depende de estado residual.
        var reproducoes = new List<PacoteReproduzido147>();
        for (var i = 0; i < 2; i++)
        {
            await using var banco = await Banco.CriarAsync();
            await RestauracaoDoBrain147.RestaurarAsync(banco.ConnectionString, evidencia.EstadoAntesDeB, timeout.Token);
            reproducoes.Add(await RestauracaoDoBrain147.ReproduzirAsync(banco.ConnectionString, evidencia.ConsultaB, timeout.Token));
        }
        var (a, b) = (reproducoes[0], reproducoes[1]);
        IgualByteAByte(a.TextoDoPacote, b.TextoDoPacote, "determinismo: texto do pacote");
        IgualByteAByte(a.EntradaB, b.EntradaB, "determinismo: entrada B");
        IgualByteAByte(a.PacoteJsonComId(evidencia.IdDoPacote), b.PacoteJsonComId(evidencia.IdDoPacote), "determinismo: pacote.json");
        var hashes = (Texto: PacoteReproduzido147.Sha256(a.TextoDoPacote), EntradaB: PacoteReproduzido147.Sha256(a.EntradaB),
            PacoteJson: PacoteReproduzido147.Sha256(a.PacoteJsonComId(evidencia.IdDoPacote)));
        output.WriteLine($"formato={(RestauracaoDoBrain147.FormatoHistorico ? "historico" : "vigente")} texto={hashes.Texto} ({a.TextoDoPacote.Length} B) entradaB={hashes.EntradaB} pacoteJson={hashes.PacoteJson}");
        // Unidades essenciais pré-registradas (CenariosDaRetomada147) presentes no pacote reconstruído, em qualquer formato.
        // Só roda onde a fixture está no diretório de saída; a prova histórica em 97bd98d copia só os arquivos .cs.
        if (File.Exists(RoteiroDaRetomada147Caminho()))
        {
            var verificacao = VerificacaoDaRetomada147.Verificar(diretorio, RoteiroDaRetomada147.Carregar());
            var essenciais = EssenciaisDaRetomada147.AvaliarPacote(Encoding.UTF8.GetString(a.TextoDoPacote), new EstadoExportado147(evidencia.EstadoAntesDeB),
                RoteiroDaRetomada147.Carregar(), "curto", verificacao.Marcador);
            output.WriteLine("essenciais: " + string.Join(", ", essenciais.Select(e => $"{e.Key}={e.Value}")));
            Assert.Equal(18, essenciais.Count);
            Assert.All(essenciais, e => Assert.True(e.Value, "Essencial ausente no pacote reconstruído: " + e.Key));
        }
        if (RestauracaoDoBrain147.FormatoHistorico)
        {
            IgualByteAByte(evidencia.TextoDoPacote, a.TextoDoPacote, "histórico: pacote-texto-para-injecao.txt");
            IgualByteAByte(evidencia.EntradaB, a.EntradaB, "histórico: entrada-b.txt");
            IgualByteAByte(evidencia.PacoteJson, a.PacoteJsonComId(evidencia.IdDoPacote), "histórico: pacote.json");
        }
        else
        {
            Assert.True(CompactoPreRegistrado.TryGetValue(id, out var esperado), "Sem hash pré-registrado do formato vigente para " + id);
            Assert.Equal(esperado, hashes);
        }
    }

    private static string RoteiroDaRetomada147Caminho() => Path.Combine(AppContext.BaseDirectory, "CenariosDaRetomada147", "cenarios.json");

    private static void IgualByteAByte(byte[] esperado, byte[] obtido, string rotulo)
    {
        if (esperado.AsSpan().SequenceEqual(obtido)) return;
        Assert.Fail($"{rotulo} diverge: " + RestauracaoDoBrain147.PrimeiraDiferenca(Encoding.UTF8.GetString(obtido), Encoding.UTF8.GetString(esperado)));
    }
}

// Precisa do PostgreSQL descartável e das evidências locais da #147 (não versionadas). Sem elas, o teste é ignorado.
internal sealed class EvidenciaHistorica147TheoryAttribute : TheoryAttribute
{
    public EvidenciaHistorica147TheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DANTE_TEST_POSTGRES")))
            Skip = "Defina DANTE_TEST_POSTGRES para PostgreSQL de teste com permissão CREATE DATABASE.";
        else if (!RestauracaoDoBrain147Tests.Historicas.Keys.All(id => Directory.Exists(RestauracaoDoBrain147Tests.Diretorio(id))))
            Skip = "Evidências históricas da #147 ausentes em " + RaizDeEvidencias147.Padrao() + ".";
    }
}
