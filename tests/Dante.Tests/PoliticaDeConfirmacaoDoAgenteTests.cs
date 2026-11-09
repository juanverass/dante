using System.Text.Json;
using Dante.Domain.CapturaDeConhecimento;
using Dante.Domain.Conhecimentos;
using Dante.Worker.Brain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Dante.Tests;

public sealed class PoliticaDeConfirmacaoDoAgenteTests
{
    [Fact]
    public async Task ContratoChegaAoHandshakeEAsDuasClisComProibicaoENovaDeclaracaoHumana()
    {
        var contrato = ContratoDoBrainParaAgentes.Instrucoes;
        Assert.Contains("Nunca ofereça confirmar, validar ou promover conclusões do próprio agente", contrato);
        Assert.Contains("que basta o usuário confirmar no Telegram", contrato);
        Assert.Contains("Não apresente essa conversão como pendência ou próximo passo", contrato);
        Assert.Contains("OUTRO conhecimento", contrato); Assert.Contains("DitoPeloUsuario", contrato);
        Assert.Contains("mensagem autenticada como proveniência", contrato); Assert.Contains("DerivadoDe", contrato);
        Assert.Contains("não muda o ID, a origem ou o status do original", contrato);
        Assert.Contains("Um simples \"confirmar\", \"concordo\" ou \"documente isso\" não é nova declaração literal", contrato);
        // O mesmo contrato de conversa é usado por Claude e Codex; sem depender da frase específica do cenário.
        Assert.Contains(contrato, Dante.Worker.Sessions.ContratoDaConversaParaAgentes.ParaSessao([]));
        using var saida = new StringWriter();
        await ServidorMcpDoBrain.ExecutarAsync(new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n"), saida,
            (_, _, _) => throw new InvalidOperationException("initialize não deve acessar armazenamento"));
        using var json = JsonDocument.Parse(saida.ToString());
        Assert.Equal(contrato, json.RootElement.GetProperty("result").GetProperty("instructions").GetString());
        foreach (var nome in new[] { "brain_buscar_conhecimento", "brain_mostrar_origem", "brain_confirmar_candidato", "brain_vincular_candidatos" })
            Assert.Contains(ContratoDoBrainParaAgentes.RegraDaConfirmacao, ServidorMcpDoBrain.Ferramentas.Single(f => f.Nome == nome).Descricao);
    }

    [PostgreSqlTheory]
    [InlineData("Para virarem fatos confirmados, você teria que confirmar os dois no Telegram.")]
    [InlineData("Para confirmar o incidente e a solução, eu preparo a proposta e você confirma no Telegram.")]
    [InlineData("Confirme a conclusão do agente e promova o mesmo registro a fato confirmado.")]
    public async Task PedidoDePromocaoNaoMudaOrigemENaoOfereceConfirmacaoEpistemica(string pedido)
    {
        await using var a = await BrainMcpConhecimentoTests.Ambiente.CriarAsync();
        var mensagem = BrainMcpTests.Mensagem(pedido);
        a.App.GetRequiredService<RegistroDeOperacoesBrain>().IniciarTurno("S1", mensagem);
        var ops = a.App.GetRequiredService<OperacoesMcpDoBrain>();
        Task<object?> Operar(string nome, object args) => ops.ExecutarAsync(a.Escopo, mensagem, "S1", "codex", nome, JsonSerializer.SerializeToElement(args));
        using var captura = JsonDocument.Parse(JsonSerializer.Serialize(await Operar("brain_capturar_conhecimento", new
        {
            tipo = "Incidente", conteudo = "A conclusão foi produzida pelo agente.", natureza = "ConclusaoDoAgente", justificativa = "Diagnóstico do agente"
        })));
        var candidato = captura.RootElement.GetProperty("candidato");
        Assert.Equal(ContratoDoBrainParaAgentes.RegraDaConfirmacao, candidato.GetProperty("orientacaoDeConfirmacao").GetString());
        var id = candidato.GetProperty("Id").GetGuid();
        using var proposta = JsonDocument.Parse(JsonSerializer.Serialize(await Operar("brain_confirmar_candidato", new { id, revisao = 1 })));
        Assert.Contains("somente registrar a conclusão como Inferido", proposta.RootElement.GetProperty("instrucao").GetString());
        Assert.Contains(ContratoDoBrainParaAgentes.RegraDaConfirmacao, proposta.RootElement.GetProperty("instrucao").GetString());
        // Review do PR #284: a orientação ao modelo pede autorização de registro, não confirmação do conteúdo.
        var instrucao = proposta.RootElement.GetProperty("instrucao").GetString()!;
        Assert.Contains("peça autorização para registrar", instrucao);
        Assert.Contains("não confirma o conteúdo", instrucao);
        Assert.DoesNotContain("Ele deve enviar confirmar", instrucao);
        Assert.True(proposta.RootElement.GetProperty("aguardandoAutorizacaoDeRegistro").GetBoolean());
        Assert.False(proposta.RootElement.TryGetProperty("aguardandoConfirmacao", out _));
        Assert.Contains("Inferido", proposta.RootElement.GetProperty("efeitoDaAutorizacao").GetString());
        Assert.Contains("peça autorização para registrar", ServidorMcpDoBrain.Ferramentas.Single(f => f.Nome == "brain_confirmar_candidato").Descricao);
        await using (var db = a.Banco.Contexto()) Assert.Empty(await db.Conhecimentos.ToListAsync());
        Assert.Contains("permanece inferido", await a.ConfirmarAsync());
        await using (var db = a.Banco.Contexto())
        {
            var conhecimento = await db.Conhecimentos.SingleAsync();
            Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, conhecimento.NaturezaDeOrigem);
            Assert.Equal(NaturezaDoConteudo.ConclusaoDoAgente, conhecimento.Natureza);
            Assert.Equal(StatusDoConhecimento.Inferido, conhecimento.Status); Assert.False(conhecimento.PodeSerConfirmado);
            Assert.Throws<InvalidOperationException>(() => conhecimento.Confirmar(conhecimento.Revisao, a.Prova, DateTimeOffset.UtcNow));
            Assert.Equal(StatusDoConhecimento.Inferido, conhecimento.Status);
        }
    }

}
