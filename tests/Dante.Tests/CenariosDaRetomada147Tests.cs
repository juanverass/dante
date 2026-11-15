using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

namespace Dante.Tests;

// Fase 0 da #147: a fixture de roteiros é pré-registrada (hash fixo, publicado na Issue), reproduz exatamente os pedidos
// das retomadas medidas em 15/11, cita só commits integrados e o verificador recusa evidência adulterada ou misturada.
public sealed class CenariosDaRetomada147Tests(ITestOutputHelper output)
{
    // Publicado na #147 antes de qualquer execução. Mudar a fixture exige novo pré-registro, nunca ajuste silencioso.
    internal const string HashDaFixture = "9ed460f1645a8f9cdc38688954bb0a5dd60eaa0f4ef21ba5befe04f449cea58b";
    // Retomada r1 de 15/11: marcador, entradas de A e referências gravadas na evidência (hashes do manifesto 7d83c7cf…).
    private const string MarcadorR1 = "verificacao147893ecbea00624d6587f0d086094bbbf1";
    private const string EntradaA1R1 = "b0b8c3bc29bce8a53373130c3dc8f0d5f8e2febe48c049771afb985978a04ff7";
    private const string EntradaA2R1 = "36be7077bfc125dba8bdbcc0ae58084958f46e769c885156700e5de9322b5553";
    private const string RefsR1 = "conhecimento:5b2b7725-430f-4b47-a840-81345807139c@2, conhecimento:75c0bbe1-a399-42f8-bfa1-e54e0f652685@1, " +
        "conhecimento:6f547be9-d8fa-4eb5-a26c-093243c928e9@1, fonte:d764bd55-3b86-4503-963f-c5da52715f12@1";
    private const string DecisaoR1 = "5b2b7725-430f-4b47-a840-81345807139c";
    // Consulta B do harness até a #289, usada nas retomadas medidas.
    private const string ConsultaHistorica = "Retome a validação de confiabilidade do D.A.N.T.E. Qual problema foi analisado, qual solução foi adotada, qual restrição permanece e qual é o próximo passo? Consulte também as relações e a origem. Separe fonte bruta, fato confirmado e conclusão do agente. Inclua o identificador verificável do ensaio. Use exclusivamente o Brain.";

    private static string Sha(string texto) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false).GetBytes(texto)));

    [Fact]
    public void FixtureTemOHashPreRegistrado() =>
        Assert.Equal(HashDaFixture, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(RoteiroDaRetomada147.Caminho))));

    [Fact]
    public void DuracoesTemDoisSeisEDozeTurnosComCasosAninhadosEIdentificadoresUnicos()
    {
        var roteiro = RoteiroDaRetomada147.Carregar();
        Assert.Equal([2, 6, 12], RoteiroDaRetomada147.Duracoes.Select(roteiro.Turnos));
        var longo = roteiro.Casos("longo");
        Assert.Equal(Enumerable.Range(1, 6), longo.Select(c => c.Ordem));
        // Curto ⊂ médio ⊂ longo: os primeiros casos são os mesmos em todas as durações.
        Assert.Equal(longo.Take(1), roteiro.Casos("curto")); Assert.Equal(longo.Take(3), roteiro.Casos("medio"));
        Assert.Equal(6, longo.Select(c => c.Pr).Distinct().Count()); Assert.Equal(6, longo.Select(c => c.Commit).Distinct().Count());
        var ids = longo.Select(c => RoteiroDaRetomada147.Identificador(c.Ordem, MarcadorR1)).ToArray();
        Assert.Equal(6, ids.Distinct().Count());
        // O identificador do caso 1 é prefixo dos demais e não pode casar com eles.
        Assert.All(ids.Skip(1), id => Assert.False(EstadoExportado147.ContemIdentificador("conteúdo " + id, ids[0])));
        Assert.True(EstadoExportado147.ContemIdentificador("conteúdo " + ids[0] + ".", ids[0]));
        foreach (var caso in longo)
        {
            var unidades = roteiro.Essenciais(caso.Ordem).Select(u => u.Id).ToArray();
            Assert.Equal(unidades.Length, unidades.Distinct().Count());
            Assert.Contains("marcador", unidades); Assert.Contains("incidente", unidades); Assert.Contains("solucao", unidades); Assert.Contains("ref_fonte", unidades);
            var md = roteiro.Markdown(caso, MarcadorR1);
            Assert.Contains(caso.Incidente, md); Assert.Contains(caso.Solucao, md); Assert.Contains($"PR #{caso.Pr}", md);
            Assert.EndsWith(ids[caso.Ordem - 1], md);
            Assert.DoesNotMatch(@"\{[a-z]+\}", roteiro.PedidoDoCaso(caso, MarcadorR1));
        }
        Assert.All(RoteiroDaRetomada147.Duracoes, d => Assert.NotEmpty(roteiro.ConsultaB(d)));
    }

    [Fact]
    public void CurtoReproduzExatamenteOsPedidosDaRetomadaMedida()
    {
        var roteiro = RoteiroDaRetomada147.Carregar();
        var caso = Assert.Single(roteiro.Casos("curto"));
        Assert.Equal(EntradaA1R1, Sha(roteiro.PedidoDoCaso(caso, MarcadorR1)));
        Assert.Equal(EntradaA2R1, Sha(roteiro.PedidoDoSnapshot(RefsR1, Guid.Parse(DecisaoR1))));
        Assert.Equal(ConsultaHistorica, roteiro.ConsultaB("curto"));
        Assert.Equal(Issue147BrainIndependenteTests.SeparadorDoHistorico, roteiro.Separador);
        Assert.Equal((RefsR1, DecisaoR1), roteiro.LerPedidoDoSnapshot(roteiro.PedidoDoSnapshot(RefsR1, Guid.Parse(DecisaoR1))));
        Assert.Null(roteiro.LerPedidoDoSnapshot(roteiro.PedidoDoSnapshot(RefsR1, Guid.Parse(DecisaoR1)) + " extra"));
    }

    [Fact]
    public void PreenchimentoNaoReinterpretaTextoInseridoERecusaPlaceholderDesconhecido()
    {
        Assert.Equal("a {ordem} b", RoteiroDaRetomada147.Preencher("a {x} b", new() { ["x"] = "{ordem}" }));
        Assert.Throws<KeyNotFoundException>(() => RoteiroDaRetomada147.Preencher("{desconhecido}", new()));
    }

    // Casos 2–6: incidente e solução são texto literal da mensagem de um commit integrado na main; o caso 1 é a fixture
    // preexistente, conferida só quanto ao commit e à PR. Espaços e quebras de linha da mensagem são normalizados.
    [RepositorioGitFact]
    public void CasosCitamCommitsIntegradosComTextoLiteral()
    {
        foreach (var caso in RoteiroDaRetomada147.Carregar().TodosOsCasos)
        {
            Assert.Equal("commit", Git("cat-file", "-t", caso.Commit).Trim());
            Assert.Equal(0, GitCodigo("merge-base", "--is-ancestor", caso.Commit, "HEAD"));
            var mensagem = Git("log", "-1", "--format=%B", caso.Commit);
            Assert.Contains($"(#{caso.Pr})", mensagem.Split('\n')[0]);
            if (caso.Ordem == 1) continue;
            var normalizada = Normalizar(mensagem);
            Assert.Contains(Normalizar(caso.Incidente), normalizada); Assert.Contains(Normalizar(caso.Solucao), normalizada);
            Assert.Contains(Normalizar(caso.Titulo), normalizada);
        }
        static string Normalizar(string t) => Regex.Replace(t, @"\s+", " ").Trim();
    }

    [EvidenciaLocal147Theory]
    [InlineData("20261115T151709Z-retomada-r1-f61fb9147a7d")]
    [InlineData("20261115T151449Z-retomada-r2-601a68d9783c")]
    public void RetomadasMedidasPassamNaVerificacaoComTodosOsEssenciaisNoPacote(string id)
    {
        var r = VerificacaoDaRetomada147.Verificar(RestauracaoDoBrain147Tests.Diretorio(id), RoteiroDaRetomada147.Carregar());
        output.WriteLine(JsonSerializer.Serialize(r));
        Assert.Equal("curto", r.Duracao);
        Assert.Empty(r.Divergencias);
        Assert.Equal(18, r.Essenciais.Count);
        Assert.All(r.Essenciais, e => Assert.True(e.Value, "Essencial ausente no pacote histórico: " + e.Key));
    }

    [EvidenciaLocal147Fact]
    public void EvidenciaAlteradaEhRecusadaSemLeituraDoConteudo()
    {
        var copia = Copiar("20261115T151709Z-retomada-r1-f61fb9147a7d");
        try
        {
            var caminho = Path.Combine(copia, "historico-visivel.txt");
            var bytes = File.ReadAllBytes(caminho); bytes[^1] ^= 1; File.WriteAllBytes(caminho, bytes);
            var r = VerificacaoDaRetomada147.Verificar(copia, RoteiroDaRetomada147.Carregar());
            Assert.Contains("evidencia: alterado: historico-visivel.txt", r.Divergencias);
            Assert.Empty(r.Essenciais);
        }
        finally { Directory.Delete(copia, true); }
    }

    // Manifesto e SHA256SUMS regravados de forma consistente: a integridade do arquivo passa, e o verificador ainda
    // precisa detectar confirmação indevida, relação removida e mistura de estado de outra execução.
    [EvidenciaLocal147Fact]
    public void VerificadorDetectaConfirmacaoIndevidaRelacaoPerdidaEEstadoMisturado()
    {
        var roteiro = RoteiroDaRetomada147.Carregar();
        var r2 = EvidenciaDeRetomada147.Ler(RestauracaoDoBrain147Tests.Diretorio("20261115T151449Z-retomada-r2-601a68d9783c"));
        string Divergencias(Func<JsonNode, string> alterar)
        {
            var copia = Copiar("20261115T151709Z-retomada-r1-f61fb9147a7d");
            try
            {
                var resultado = JsonNode.Parse(File.ReadAllText(Path.Combine(copia, "resultado.json")))!;
                var estado = alterar(JsonNode.Parse(resultado["estadoAntesDeB"]!.GetValue<string>())!);
                resultado["estadoAntesDeB"] = estado; resultado["estadoDepoisDeB"] = estado;
                RegravarComManifesto(copia, "resultado.json", resultado.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                var r = VerificacaoDaRetomada147.Verificar(copia, roteiro);
                Assert.DoesNotContain(r.Divergencias, d => d.StartsWith("evidencia:", StringComparison.Ordinal));
                return string.Join("\n", r.Divergencias);
            }
            finally { Directory.Delete(copia, true); }
        }
        // Sem alteração, a regravação não introduz divergência.
        Assert.Equal("", Divergencias(e => e.ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
        Assert.Contains("conhecimento confirmado indevidamente", Divergencias(e =>
        {
            foreach (var k in e["conhecimentos"]!.AsArray()) if (k!["Tipo"]!.GetValue<int>() != 1) k["Status"] = 0;
            return e.ToJsonString();
        }));
        Assert.Contains("relação ResolvidoPor incidente→solução ausente", Divergencias(e => { e["relacoes"]!.AsArray().Clear(); return e.ToJsonString(); }));
        var misturado = Divergencias(_ => r2.EstadoAntesDeB);
        Assert.Contains("fonte literal ausente", misturado);
        Assert.Contains("referência sem registro no estado", misturado);
    }

    [EvidenciaHistorica147Theory]
    [InlineData("20261115T151709Z-retomada-r1-f61fb9147a7d")]
    public async Task RestauracaoRecusaExportIncompletoColecaoExtraEBancoComDados(string id)
    {
        var evidencia = EvidenciaDeRetomada147.Ler(RestauracaoDoBrain147Tests.Diretorio(id));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var semHash = JsonNode.Parse(evidencia.EstadoAntesDeB)!; semHash["fontes"]![0]!.AsObject().Remove("Hash");
        var extra = JsonNode.Parse(evidencia.EstadoAntesDeB)!; extra["avaliacoes"] = new JsonArray();
        await using (var banco = await Banco.CriarAsync())
        {
            var erro = await Assert.ThrowsAsync<InvalidDataException>(() => RestauracaoDoBrain147.RestaurarAsync(banco.ConnectionString, semHash.ToJsonString(), timeout.Token));
            Assert.Contains("DocumentoFonte.Hash ausente do export", erro.Message);
        }
        await using (var banco = await Banco.CriarAsync())
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => RestauracaoDoBrain147.RestaurarAsync(banco.ConnectionString, extra.ToJsonString(), timeout.Token));
            await RestauracaoDoBrain147.RestaurarAsync(banco.ConnectionString, evidencia.EstadoAntesDeB, timeout.Token);
            await Assert.ThrowsAsync<InvalidOperationException>(() => RestauracaoDoBrain147.RestaurarAsync(banco.ConnectionString, evidencia.EstadoAntesDeB, timeout.Token));
        }
    }

    private static string Copiar(string id)
    {
        var destino = Directory.CreateTempSubdirectory("dante147-verificacao-").FullName;
        foreach (var arquivo in Directory.EnumerateFiles(RestauracaoDoBrain147Tests.Diretorio(id)))
            File.Copy(arquivo, Path.Combine(destino, Path.GetFileName(arquivo)));
        // O id da execução é o nome do diretório; a cópia precisa manter esse nome dentro do temporário.
        var final = Path.Combine(destino, id); Directory.CreateDirectory(final);
        foreach (var arquivo in Directory.EnumerateFiles(destino)) File.Move(arquivo, Path.Combine(final, Path.GetFileName(arquivo)));
        return final;
    }

    // Reescreve um arquivo e mantém manifesto e SHA256SUMS coerentes (formato "<sha256>  <nome>\n" por arquivo do manifesto).
    private static void RegravarComManifesto(string diretorio, string nome, string conteudo)
    {
        var bytes = new UTF8Encoding(false).GetBytes(conteudo);
        File.WriteAllBytes(Path.Combine(diretorio, nome), bytes);
        var caminho = Path.Combine(diretorio, "manifesto.json");
        var manifesto = JsonNode.Parse(File.ReadAllText(caminho))!;
        foreach (var a in manifesto["arquivos"]!.AsArray())
            if (a!["nome"]!.GetValue<string>() == nome) { a["bytes"] = bytes.LongLength; a["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)); }
        File.WriteAllText(caminho, manifesto.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var somas = Path.Combine(diretorio, "SHA256SUMS");
        File.WriteAllText(somas, string.Concat(manifesto["arquivos"]!.AsArray().Select(a => $"{a!["sha256"]}  {a["nome"]}\n")), new UTF8Encoding(false));
    }

    internal static string? RaizDoRepositorio()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Dante.sln"))) return dir.FullName;
        return null;
    }
    private static (int Codigo, string Saida) Executar(params string[] argumentos)
    {
        var inicio = new ProcessStartInfo("git") { WorkingDirectory = RaizDoRepositorio()!, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in argumentos) inicio.ArgumentList.Add(a);
        using var p = Process.Start(inicio)!;
        var saida = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
        return (p.ExitCode, saida);
    }
    private static string Git(params string[] argumentos) { var (c, s) = Executar(argumentos); Assert.Equal(0, c); return s; }
    private static int GitCodigo(params string[] argumentos) => Executar(argumentos).Codigo;
}

// Verificação das evidências locais da #147 (não versionadas), sem banco. Sem elas, o teste é ignorado.
internal sealed class EvidenciaLocal147TheoryAttribute : TheoryAttribute
{
    public EvidenciaLocal147TheoryAttribute() { if (EvidenciaLocal147FactAttribute.Ausente() is { } motivo) Skip = motivo; }
}
internal sealed class EvidenciaLocal147FactAttribute : FactAttribute
{
    public EvidenciaLocal147FactAttribute() { if (Ausente() is { } motivo) Skip = motivo; }
    internal static string? Ausente() => RestauracaoDoBrain147Tests.Historicas.Keys.All(id => Directory.Exists(RestauracaoDoBrain147Tests.Diretorio(id)))
        ? null : "Evidências históricas da #147 ausentes em " + RaizDeEvidencias147.Padrao() + ".";
}
// Export sem .git (por exemplo, git archive) não tem histórico para conferir os commits citados.
internal sealed class RepositorioGitFactAttribute : FactAttribute
{
    public RepositorioGitFactAttribute()
    {
        var raiz = CenariosDaRetomada147Tests.RaizDoRepositorio();
        if (raiz is null || !Path.Exists(Path.Combine(raiz, ".git"))) Skip = "Sem repositório Git para conferir os commits citados.";
    }
}
