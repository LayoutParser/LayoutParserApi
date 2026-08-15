using System.Text.RegularExpressions;

namespace LayoutParserApi.Tests.Infra
{
    /// <summary>
    /// Guarda de regressão do quality gate do deploy de PRODUÇÃO (<c>.github/workflows/deploy.yml</c>).
    ///
    /// Contexto: até 2026-08-14 só o <c>ci-dev.yml</c> rodava <c>dotnet test</c> — produção buildava e
    /// publicava sem nunca executar a suíte. O caminho que garantia o gate (feat → develop → master via
    /// PR) deixou de ser obrigatório quando a branch protection nativa do GitHub foi perdida ao tornar os
    /// repositórios privados (ver <c>.claude/rules/agent-authority.md</c>). Sem bloqueio técnico do
    /// GitHub, o que impede a regressão é este teste.
    ///
    /// O que ele trava: (1) o deploy de produção executa a suíte de fato; (2) essa execução acontece
    /// ANTES de qualquer step que escreva no host; (3) o gate não é tolerante a falha; (4) deploys
    /// concorrentes continuam serializados. As asserções são textuais de propósito — o alvo é um YAML,
    /// não código. Se o workflow for legitimamente reestruturado (ex.: a suíte virar um job separado com
    /// <c>needs:</c>), este teste precisa ser atualizado junto: a mensagem de falha diz o porquê.
    /// </summary>
    public class DeployWorkflowGateTests
    {
        /// <summary>
        /// Invocação REAL de <c>dotnet test</c> — início de linha, dentro de um bloco <c>run:</c>.
        /// Menções em comentário (<c>`dotnet test`</c>, <c># dotnet test ...</c>) não casam de propósito:
        /// um gate removido que deixou o comentário para trás não pode passar por gate existente.
        /// </summary>
        private static readonly Regex InvocacaoDeDotnetTest =
            new(@"^[ \t]*dotnet test\b", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        /// Marcadores de "daqui em diante o workflow ESCREVE no host de produção". São trechos de
        /// comando, não nomes de step: nome se renomeia sem mudar comportamento, comando não.
        /// </summary>
        private static readonly string[] MarcadoresDeEscritaEmProducao =
        {
            "LayoutParserLowCodeRunner.exe",                // publicação do runner na Bin do Sysmiddle
            "HKLM:\\SYSTEM\\CurrentControlSet\\Services",   // Environment do serviço Windows
            "Stop-Service"                                  // parada do serviço antes da cópia
        };

        [Fact]
        public void Deploy_de_producao_executa_a_suite_de_testes()
        {
            var workflow = LerDeployYml();
            if (workflow is null) return;

            Assert.True(
                InvocacaoDeDotnetTest.IsMatch(workflow),
                "deploy.yml nao invoca 'dotnet test' em nenhum bloco run:. O deploy de PRODUCAO voltaria " +
                "a publicar sem que a suite tenha rodado - e, sem branch protection, um push direto em " +
                "master chegaria ao servidor sem nenhum teste automatizado.");
        }

        [Fact]
        public void Gate_de_testes_vem_antes_de_qualquer_step_que_toca_producao()
        {
            var workflow = LerDeployYml();
            if (workflow is null) return;

            var invocacao = InvocacaoDeDotnetTest.Match(workflow);
            Assert.True(invocacao.Success, "deploy.yml nao invoca 'dotnet test' em nenhum bloco run:.");

            foreach (var marcador in MarcadoresDeEscritaEmProducao)
            {
                var posicaoDaEscrita = workflow.IndexOf(marcador, StringComparison.Ordinal);
                if (posicaoDaEscrita < 0) continue; // step removido/renomeado: nada a comparar

                Assert.True(
                    invocacao.Index < posicaoDaEscrita,
                    $"'dotnet test' aparece DEPOIS de '{marcador}' em deploy.yml. O gate precisa rodar " +
                    "antes de qualquer escrita no host: este workflow nao tem swap atomico nem backup " +
                    "dos binarios anteriores, entao abortar no meio deixa producao pela metade.");
            }
        }

        [Fact]
        public void Gate_de_testes_nao_e_tolerante_a_falha()
        {
            var workflow = LerDeployYml();
            if (workflow is null) return;

            var bloco = ExtrairStepQueRodaOsTestes(workflow);
            Assert.NotNull(bloco);

            // continue-on-error transformaria o gate em enfeite: teste vermelho e o deploy segue.
            Assert.DoesNotContain("continue-on-error", bloco, StringComparison.Ordinal);
        }

        [Fact]
        public void Deploys_de_producao_continuam_serializados()
        {
            var workflow = LerDeployYml();
            if (workflow is null) return;

            Assert.Contains("concurrency:", workflow, StringComparison.Ordinal);

            // cancel-in-progress: false é deliberado — cancelar um deploy durante a troca de binários
            // (Stop-Service → cópia → Start-Service) deixaria a instalação inconsistente.
            Assert.Contains("cancel-in-progress: false", workflow, StringComparison.Ordinal);
        }

        // ---- Apoio -------------------------------------------------------------------------------

        /// <summary>
        /// Sobe a árvore de diretórios a partir do binário de teste até achar o workflow, e devolve o
        /// conteúdo com as linhas de COMENTÁRIO neutralizadas (ver <see cref="SemComentarios"/>).
        /// Retorna <c>null</c> quando o repositório não está por perto (execução a partir de um
        /// diretório publicado, por exemplo) — nesse cenário o teste não tem o que afirmar, e falhar
        /// seria um vermelho falso justamente dentro do gate que ele protege.
        /// </summary>
        private static string? LerDeployYml()
        {
            var diretorio = new DirectoryInfo(AppContext.BaseDirectory);

            while (diretorio is not null)
            {
                var candidato = Path.Combine(diretorio.FullName, ".github", "workflows", "deploy.yml");
                if (File.Exists(candidato))
                {
                    return SemComentarios(File.ReadAllText(candidato));
                }

                diretorio = diretorio.Parent;
            }

            return null;
        }

        /// <summary>
        /// Substitui cada linha de comentário (YAML ou PowerShell dentro do <c>run:</c>) por espaços de
        /// mesmo comprimento — os índices do arquivo continuam valendo, mas comentário deixa de casar.
        ///
        /// Isto não é preciosismo: o deploy.yml é fortemente comentado, e menções como
        /// "Stop-Service -> copia -> Start-Service" ou "cancel-in-progress: false de PROPOSITO"
        /// aparecem em comentário ANTES do comportamento real. Sem neutralizá-las, o teste afirmaria
        /// sobre prosa em vez de sobre o workflow — passaria com o gate deletado e o comentário intacto.
        /// </summary>
        private static string SemComentarios(string workflow)
        {
            var linhas = workflow.Split('\n');

            for (var i = 0; i < linhas.Length; i++)
            {
                if (linhas[i].TrimStart().StartsWith('#'))
                {
                    linhas[i] = new string(' ', linhas[i].Length);
                }
            }

            return string.Join('\n', linhas);
        }

        /// <summary>
        /// Recorta o bloco YAML do step que roda a suíte (do <c>- name:</c> anterior ao próximo).
        /// </summary>
        private static string? ExtrairStepQueRodaOsTestes(string workflow)
        {
            const string inicioDeStep = "\n    - name:";

            var invocacao = InvocacaoDeDotnetTest.Match(workflow);
            if (!invocacao.Success) return null;

            var inicio = workflow.LastIndexOf(inicioDeStep, invocacao.Index, StringComparison.Ordinal);
            if (inicio < 0) return null;

            var fim = workflow.IndexOf(inicioDeStep, invocacao.Index, StringComparison.Ordinal);
            if (fim < 0) fim = workflow.Length;

            return workflow[inicio..fim];
        }
    }
}
