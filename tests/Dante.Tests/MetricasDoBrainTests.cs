using Dante.Application.ConversaDoBrain;
using Dante.Application.MetricasDoBrain;
using Dante.Infrastructure.MetricasDoBrain;
using Dante.Worker.Sessions;
using Dante.Worker.Telegram;
using Microsoft.Extensions.Configuration;
namespace Dante.Tests;

// #148: métricas locais de continuidade, recuperação e contexto, sem conteúdo; dado ausente fica indisponível e o
// veredito usa limiares fixos sobre o histórico bruto das sessões anteriores do escopo.
public sealed class MetricasDoBrainTests
{
    private static readonly DateTimeOffset Inicio=new(2026,10,6,12,0,0,TimeSpan.Zero);

    [Theory]
    [InlineData("avalie a retomada: repetições: 0; concluída: sim",IntencaoDoBrain.AvaliarRetomada,"repetições: 0; concluída: sim")]
    [InlineData("avaliar retomada",IntencaoDoBrain.AvaliarRetomada,"")]
    [InlineData("métricas do Brain",IntencaoDoBrain.Metricas,"")]
    [InlineData("mostre as métricas do brain?",IntencaoDoBrain.Metricas,"")]
    public void MedicaoTemIntencoesProprias(string texto,IntencaoDoBrain esperado,string corpo)
    {
        var intencao=ResolvedorDeIntencaoDoBrain.Resolver(texto);Assert.Equal(esperado,intencao.Intencao);Assert.Equal(corpo,intencao.Texto);
    }

    [Theory]
    [InlineData(300,"ganho")]
    [InlineData(500,"ganho")]
    [InlineData(700,"neutralidade")]
    [InlineData(1000,"neutralidade")]
    [InlineData(1500,"regressão")]
    public void RetomadaComparaOContextoDoBrainComOHistoricoAnterior(int pacote,string indicacao)
    {
        // Sessão A: 100 + 900 tokens de histórico bruto, sem pacote. Sessão B: retomada com pacote no bootstrap e refresh vazio.
        var resumo=MetricasDoBrainAppService.Resumir([Envio("a",0,100),Turno("a",1,900),Envio("b",10,50,3,pacote),Envio("b",12,20,boot:false),Turno("b",13,200,5000)]);
        Assert.Equal(indicacao,resumo.Indicacao);
        var retomada=Assert.Single(resumo.Retomadas);Assert.Equal("b",retomada.IdSessao);Assert.Equal(1000,retomada.TokensDeReferencia);Assert.Equal(pacote,retomada.TokensDoBrain);
        Assert.Equal(2,resumo.Sessoes);Assert.Equal(3,resumo.Envios);Assert.Equal(2,resumo.EnviosIniciais);Assert.Equal(3,resumo.Injetados);
    }

    [Fact]
    public void SemRetomadaOuSemUsoOsDadosFicamIndisponiveis()
    {
        // Primeira sessão com pacote não é retomada: não há histórico anterior para comparar.
        var resumo=MetricasDoBrainAppService.Resumir([Envio("a",0,100,2,200),Turno("a",1,900)]);
        Assert.Null(resumo.Razao);Assert.StartsWith("dados insuficientes",resumo.Indicacao);
        Assert.Null(resumo.EntradaReportada);Assert.Equal(1,resumo.TurnosSemUsoReportado);Assert.Null(resumo.Precisao);
        var texto=MetricasDoBrainAppService.Formatar(resumo);
        Assert.Contains("entrada indisponível, saída indisponível (1 turno(s) sem dado)",texto);Assert.Contains("Retomadas: 0.",texto);
        Assert.Contains("Indicação: dados insuficientes",texto);
    }

    [Fact]
    public void AvaliacaoAlimentaQualidadeEPrecisaoSemPresumirZero()
    {
        var avaliacao=new MetricaDoBrainDto{Tipo=MetricaDoBrainDto.Avaliacao,Em=Inicio.AddMinutes(20),IdSessao="b",Repeticoes=1,Concluida=false,Relevantes=2,Irrelevantes=1,Incorretos=1};
        var resumo=MetricasDoBrainAppService.Resumir([Envio("a",0,100),Turno("a",1,900),Envio("b",10,50,3,300),Turno("b",13,200,5000),avaliacao]);
        Assert.Equal(0.5,resumo.Precisao);Assert.Equal(5000,resumo.EntradaReportada);
        Assert.Contains("1 informação(ões) essencial(is) repetida(s)",resumo.AlertasDeQualidade);Assert.Contains("1 tarefa(s) não concluída(s)",resumo.AlertasDeQualidade);
        Assert.Contains("1 item(ns) incorreto(s) ou obsoleto(s) injetado(s)",resumo.AlertasDeQualidade);
        var texto=MetricasDoBrainAppService.Formatar(resumo);
        Assert.Contains("Indicação: ganho (Brain/histórico = 0,30; ganho ≤ 0,50, neutralidade ≤ 1,00).",texto);
        Assert.Contains("- b (Codex): Brain 300 tokens × histórico de referência 1.000 = 0,30; repetições 1, esclarecimentos ?, concluída não, contexto adicional ?",texto);
        Assert.Contains("precisão avaliada 0,50",texto);Assert.Contains("Armazenado: 4 conhecimento(s), 4.000 caracteres",texto);
    }

    [Fact]
    public async Task RegistroLocalFiltraEscopoIgnoraLinhaInvalidaENaoExpoeOArquivo()
    {
        var caminho=Path.Combine(Path.GetTempPath(),"dante-metricas-"+Guid.NewGuid().ToString("N"),"metricas.jsonl");
        try
        {
            var registro=new RegistroDeMetricasEmArquivo(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["DANTE_BRAIN_METRICS_FILE"]=caminho}).Build());
            var tenant=Guid.NewGuid();var usuario=Guid.NewGuid();var espaco=Guid.NewGuid();var projeto=Guid.NewGuid();
            await registro.RegistrarAsync(Envio("a",0,10) with{IdTenant=tenant,IdUsuario=usuario,IdEspacoDeConhecimento=espaco});
            await registro.RegistrarAsync(Envio("b",1,10) with{IdTenant=tenant,IdUsuario=usuario,IdEspacoDeConhecimento=espaco,IdProjeto=projeto});
            await registro.RegistrarAsync(Envio("c",2,10) with{IdTenant=tenant,IdUsuario=Guid.NewGuid(),IdEspacoDeConhecimento=espaco});
            await File.AppendAllTextAsync(caminho,"{linha corrompida\n");
            Assert.Equal("a",Assert.Single(await registro.ListarAsync(tenant,usuario,espaco,null)).IdSessao);
            Assert.Equal("b",Assert.Single(await registro.ListarAsync(tenant,usuario,espaco,projeto)).IdSessao);
            Assert.DoesNotContain("\"Avaliacao\"",await File.ReadAllTextAsync(caminho));
            if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(caminho));
        }
        finally{Directory.Delete(Path.GetDirectoryName(caminho)!,true);}
    }

    [Fact]
    public async Task SinkRepassaEventosEMedeARespostaDoTurno()
    {
        var proximo=new SinkGravado();var continuidade=new Continuidade();var sink=new MetricasDeSessaoDoBrain(proximo,continuidade);
        var sessao=new AgentSessionSnapshot("S000001",AgentKind.Codex,123,JobExecutionContext.General("/tmp"),AgentPermissionProfile.Manual,
            AgentSessionState.Running,"T000001",0,[],true,DateTimeOffset.UtcNow,null,null);
        await sink.PublishAsync(sessao,new MessageCompletedEvent("m1","ação"),CancellationToken.None);
        await sink.PublishAsync(sessao,new MessageCompletedEvent("m2",new string('x',10)),CancellationToken.None);
        await sink.PublishAsync(sessao,new TurnCompletedEvent(AgentTurnOutcome.Completed,Usage:new(100,7,90)),CancellationToken.None);
        Assert.Equal(3,proximo.Eventos.Count);
        var turno=Assert.Single(continuidade.Turnos);
        Assert.Equal(("S000001","Codex",6,AgentTurnOutcome.Completed,new AgentTokenUsage(100,7,90)),turno);
        await sink.PublishAsync(sessao,new TurnCompletedEvent(AgentTurnOutcome.Failed,"erro"),CancellationToken.None);
        Assert.Equal(0,continuidade.Turnos[1].Tokens);Assert.Null(continuidade.Turnos[1].Uso);
    }

    private static MetricaDoBrainDto Envio(string sessao,int minuto,int pedido,int injetados=0,int pacote=0,bool boot=true)=>new()
    {
        Tipo=MetricaDoBrainDto.Envio,Em=Inicio.AddMinutes(minuto),IdSessao=sessao,Bootstrap=boot,TokensDoPedido=pedido,Injetados=injetados,Selecionados=injetados,
        Recuperados=injetados+2,Descartados=2,TokensDoPacote=pacote,CaracteresDoPacote=pacote*3,ConhecimentosNoEscopo=4,CaracteresNoEscopo=4000
    };
    private static MetricaDoBrainDto Turno(string sessao,int minuto,int resposta,long? entrada=null)=>new()
    {
        Tipo=MetricaDoBrainDto.Turno,Em=Inicio.AddMinutes(minuto),IdSessao=sessao,Agente="Codex",Resultado="Completed",TokensDaResposta=resposta,
        EntradaReportada=entrada,SaidaReportada=entrada is null?null:10
    };

    private sealed class SinkGravado:IAgentSessionEventSink
    {
        public List<AgentEvent> Eventos{get;}=[];
        public Task PublishAsync(AgentSessionSnapshot session,AgentEvent agentEvent,CancellationToken cancellationToken){Eventos.Add(agentEvent);return Task.CompletedTask;}
    }
    private sealed class Continuidade:IContinuidadeDoBrain
    {
        public List<(string Sessao,string Agente,int Tokens,AgentTurnOutcome Resultado,AgentTokenUsage? Uso)> Turnos{get;}=[];
        public bool Configurado=>true;
        public Task<ContextoParaTurno?> PrepararAsync(TelegramMessage mensagem,string idSessao,string texto,CancellationToken cancellationToken=default)=>Task.FromResult<ContextoParaTurno?>(null);
        public Task RegistrarInjecaoAsync(ContextoParaTurno contexto,CancellationToken cancellationToken=default)=>Task.CompletedTask;
        public Task RegistrarTurnoAsync(string idSessao,string agente,int tokensDaResposta,AgentTurnOutcome resultado,AgentTokenUsage? uso,CancellationToken cancellationToken=default)
        {Turnos.Add((idSessao,agente,tokensDaResposta,resultado,uso));return Task.CompletedTask;}
        public void ReiniciarSessao(string idSessao){}
        public string? EscopoSelecionado(TelegramMessage mensagem)=>null;
        public string? DescreverStatus(TelegramMessage mensagem,string? idSessao)=>null;
    }
}
