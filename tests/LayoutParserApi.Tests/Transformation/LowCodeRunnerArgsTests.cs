using LayoutParserLowCodeRunner;

namespace LayoutParserApi.Tests.Transformation
{
    /// <summary>
    /// Protocolo de linha de comando entre a API e o runner low-code.
    ///
    /// <para>Até 2026-08-10 os dois lados falavam protocolos DIFERENTES: o runner só entendia a
    /// forma posicional (<c>&lt;globalFolder&gt; &lt;package&gt; &lt;mapperGuid&gt; &lt;input&gt;
    /// &lt;output&gt;</c>) e a API sempre invocou com flags nomeadas
    /// (<c>--sysmiddleDir --globalFolder ...</c>, em <c>LowCodeTransformationService</c>). O runner
    /// lia <c>args[0]</c> (o literal "--sysmiddleDir") como globalFolder e <c>args[3]</c> (o VALOR
    /// do --globalFolder) como input, e o sintoma no log local era
    /// <c>exit=4 Input nao encontrado: C:\inetpub\wwwroot\layoutparser\globalfolder</c>.</para>
    ///
    /// <para>A suíte trava as três invariantes: (1) a forma nomeada é entendida e nada desloca,
    /// (2) a forma posicional — usada offline para gerar gabaritos, incluindo SWEEP — continua
    /// idêntica, e (3) o que o parser não reconhece VIRA ERRO com exit code próprio, nunca
    /// deslocamento silencioso.</para>
    /// </summary>
    public class LowCodeRunnerArgsTests
    {
        // Reproduz exatamente o vetor montado por LowCodeTransformationService.TransformAsync
        // (as aspas do Quote() são consumidas pelo CommandLineToArgvW antes de chegar no Main).
        private static string[] LinhaDeComandoDaApi(string mapperFlag = "--mapperId", string mapperValor = "MAP_1234")
        {
            return new[]
            {
                "--sysmiddleDir",  @"C:\inetpub\wwwroot\layoutparser\sysmiddle",
                "--globalFolder",  @"C:\inetpub\wwwroot\layoutparser\globalfolder",
                "--package",       "PAC_266bc578-b0fa-48a4-9c72-61004b729576",
                "--inputFile",     @"C:\Temp\layoutparser-lowcode\in_ab12.txt",
                "--outputFile",    @"C:\Temp\layoutparser-lowcode\out_cd34.xml",
                "--fileName",      "NFE_FIAT_20260810.txt",
                "--correlationId", "corr-abc-123",
                "--runnerLogFile", @"C:\inetpub\wwwroot\layoutparser\logs\layoutparserlowcoderunner.log",
                mapperFlag,        mapperValor
            };
        }

        // ─────────────────────────── forma nomeada (o que a API fala) ───────────────────────────

        /// <summary>
        /// O defeito original, travado: a linha REAL da API tem que produzir os campos certos.
        /// Se o parse voltar a ser posicional, GlobalFolder vira "--sysmiddleDir" e InputPath vira
        /// o caminho do globalfolder — exatamente o exit=4 observado em produção.
        /// </summary>
        [Fact]
        public void Linha_de_comando_da_api_nao_desloca_argumentos()
        {
            var r = RunnerArgsParser.Parse(LinhaDeComandoDaApi());

            Assert.True(r.Success);
            Assert.Equal(RunnerExitCodes.Ok, r.ExitCode);
            Assert.Equal(RunnerMode.Exec, r.Args.Mode);

            Assert.Equal(@"C:\inetpub\wwwroot\layoutparser\globalfolder", r.Args.GlobalFolder);
            Assert.Equal(@"C:\Temp\layoutparser-lowcode\in_ab12.txt", r.Args.InputPath);
            Assert.Equal(@"C:\Temp\layoutparser-lowcode\out_cd34.xml", r.Args.OutputPath);
            Assert.Equal("PAC_266bc578-b0fa-48a4-9c72-61004b729576", r.Args.Package);
            Assert.Equal("MAP_1234", r.Args.MapperGuid);

            // O sintoma histórico, negado explicitamente.
            Assert.NotEqual("--sysmiddleDir", r.Args.GlobalFolder);
            Assert.NotEqual(@"C:\inetpub\wwwroot\layoutparser\globalfolder", r.Args.InputPath);
        }

        /// <summary>
        /// --fileName é o motivo pelo qual a forma nomeada foi mantida: o input da API é um
        /// temporário (in_&lt;guid&gt;.txt) e o nome real do documento do usuário só chega por aqui.
        /// </summary>
        [Fact]
        public void FileName_correlationId_e_runnerLogFile_chegam_ao_runner()
        {
            var r = RunnerArgsParser.Parse(LinhaDeComandoDaApi());

            Assert.Equal("NFE_FIAT_20260810.txt", r.Args.FileName);
            Assert.Equal("corr-abc-123", r.Args.CorrelationId);
            Assert.Equal(@"C:\inetpub\wwwroot\layoutparser\logs\layoutparserlowcoderunner.log", r.Args.RunnerLogFile);
        }

        /// <summary>
        /// --sysmiddleDir é aceito e ignorado (o probing do .NET Framework resolve pelo APP BASE),
        /// mas ACEITO é o ponto: ignorar não pode significar "argumento desconhecido", e muito menos
        /// deslocar o resto do vetor.
        /// </summary>
        [Fact]
        public void SysmiddleDir_e_aceito_e_capturado_sem_afetar_o_resto()
        {
            var r = RunnerArgsParser.Parse(LinhaDeComandoDaApi());

            Assert.True(r.Success);
            Assert.Equal(@"C:\inetpub\wwwroot\layoutparser\sysmiddle", r.Args.SysmiddleDir);
            Assert.Equal(@"C:\inetpub\wwwroot\layoutparser\globalfolder", r.Args.GlobalFolder);
        }

        /// <summary>
        /// A API manda --mapperName quando o chamador informou o nome
        /// (TransformationExecutionController aceita mapperId OU mapperName).
        /// </summary>
        [Fact]
        public void MapperName_e_alternativa_valida_a_mapperId()
        {
            var r = RunnerArgsParser.Parse(LinhaDeComandoDaApi("--mapperName", "Mapeador NFe Fiat"));

            Assert.True(r.Success);
            Assert.Equal("Mapeador NFe Fiat", r.Args.MapperName);
            Assert.Null(r.Args.MapperGuid);
        }

        /// <summary>
        /// --fileName ausente: o parser devolve null e o Program cai no comportamento antigo
        /// (Path.GetFileName do input). Nada de string vazia, que viraria nome de documento vazio.
        /// </summary>
        [Fact]
        public void FileName_ausente_fica_nulo_para_cair_no_comportamento_antigo()
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--globalFolder", @"C:\gf",
                "--inputFile",    @"C:\in.txt",
                "--outputFile",   @"C:\out.xml",
                "--mapperId",     "MAP_1"
            });

            Assert.True(r.Success);
            Assert.Null(r.Args.FileName);
        }

        /// <summary>
        /// Requisito central do --fileName: quando ele vem, é ELE que vai ao Sysmiddle. O input da
        /// API é sempre um temporário in_&lt;guid&gt;.txt, então usar o nome do arquivo em disco
        /// descartaria o nome real do documento do usuário.
        /// </summary>
        [Fact]
        public void FileName_informado_vence_o_nome_do_arquivo_temporario()
        {
            var r = RunnerArgsParser.Parse(LinhaDeComandoDaApi());

            Assert.Equal("NFE_FIAT_20260810.txt", r.Args.ResolveDocumentName());
            Assert.NotEqual("in_ab12.txt", r.Args.ResolveDocumentName());
        }

        /// <summary>
        /// --fileName ausente cai no comportamento antigo (Path.GetFileName do input) — é o que
        /// mantém o CLI posicional/offline funcionando exatamente como sempre funcionou.
        /// </summary>
        [Fact]
        public void FileName_ausente_cai_no_nome_do_arquivo_de_entrada()
        {
            var posicional = RunnerArgsParser.Parse(
                new[] { @"C:\gf", "PAC_1", "MAP_9", @"C:\docs\pedido.txt", @"C:\out.xml" });

            Assert.Equal("pedido.txt", posicional.Args.ResolveDocumentName());
        }

        /// <summary>
        /// Vazio e whitespace contam como AUSENTE. A API monta
        /// <c>--fileName (fileName ?? Path.GetFileName(inputPath))</c>: um fileName string vazia
        /// (não nula) chegaria como "" e viraria nome de documento vazio no Sysmiddle.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void FileName_vazio_ou_em_branco_nao_vira_nome_de_documento(string fileName)
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--globalFolder", @"C:\gf",
                "--inputFile",    @"C:\tmp\in_ab12.txt",
                "--outputFile",   @"C:\out.xml",
                "--fileName",     fileName,
                "--mapperId",     "MAP_1"
            });

            Assert.True(r.Success);
            Assert.Equal("in_ab12.txt", r.Args.ResolveDocumentName());
        }

        /// <summary>
        /// A API manda <c>--package ""</c> quando LowCode:Package não está configurado (é o caso do
        /// appsettings atual). Valor vazio é legítimo — rejeitá-lo quebraria a instalação real.
        /// </summary>
        [Fact]
        public void Valor_vazio_e_aceito_porque_a_api_manda_package_vazio()
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--globalFolder", @"C:\gf",
                "--package",      "",
                "--inputFile",    @"C:\in.txt",
                "--outputFile",   @"C:\out.xml",
                "--mapperId",     "MAP_1"
            });

            Assert.True(r.Success);
            Assert.Equal("", r.Args.Package);
            Assert.Equal(@"C:\in.txt", r.Args.InputPath);
        }

        [Fact]
        public void Nome_de_flag_e_case_insensitive()
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--GLOBALFOLDER", @"C:\gf",
                "--InputFile",    @"C:\in.txt",
                "--outputfile",   @"C:\out.xml",
                "--MapperId",     "MAP_1"
            });

            Assert.True(r.Success);
            Assert.Equal(@"C:\gf", r.Args.GlobalFolder);
            Assert.Equal("MAP_1", r.Args.MapperGuid);
        }

        // ────────────────────── forma posicional (uso offline / gabaritos) ──────────────────────

        /// <summary>
        /// O CLI posicional é usado offline para gerar gabaritos. Quebrá-lo quebraria esse fluxo,
        /// então a ordem dos slots é contrato.
        /// </summary>
        [Fact]
        public void Posicional_single_shot_continua_identico()
        {
            var r = RunnerArgsParser.Parse(new[] { @"C:\gf", "PAC_1", "MAP_9", @"C:\in.txt", @"C:\out.xml" });

            Assert.True(r.Success);
            Assert.Equal(RunnerMode.Exec, r.Args.Mode);
            Assert.Equal(@"C:\gf", r.Args.GlobalFolder);
            Assert.Equal("PAC_1", r.Args.Package);
            Assert.Equal("MAP_9", r.Args.MapperGuid);
            Assert.Equal(@"C:\in.txt", r.Args.InputPath);
            Assert.Equal(@"C:\out.xml", r.Args.OutputPath);
        }

        [Fact]
        public void Posicional_LIST_vira_modo_de_descoberta()
        {
            var r = RunnerArgsParser.Parse(new[] { @"C:\gf", "PAC_1", "LIST", "-", "-" });

            Assert.True(r.Success);
            Assert.Equal(RunnerMode.List, r.Args.Mode);
            Assert.Equal("PAC_1", r.Args.Package);
        }

        [Fact]
        public void Posicional_SWEEP_continua_identico()
        {
            var r = RunnerArgsParser.Parse(new[] { "SWEEP", @"C:\gf", "PAC_1", "MAP_9", @"C:\examples", @"C:\saida" });

            Assert.True(r.Success);
            Assert.Equal(RunnerMode.Sweep, r.Args.Mode);
            Assert.Equal(@"C:\gf", r.Args.GlobalFolder);
            Assert.Equal("PAC_1", r.Args.Package);
            Assert.Equal("MAP_9", r.Args.MapperGuid);
            Assert.Equal(@"C:\examples", r.Args.ExamplesFolder);
            Assert.Equal(@"C:\saida", r.Args.OutputFolder);

            // SWEEP não usa os slots de arquivo — se aparecerem preenchidos, o vetor deslocou.
            Assert.Null(r.Args.InputPath);
            Assert.Null(r.Args.OutputPath);
        }

        [Fact]
        public void Posicional_SWEEP_e_case_insensitive()
        {
            var r = RunnerArgsParser.Parse(new[] { "sweep", @"C:\gf", "PAC_1", "MAP_9", @"C:\examples", @"C:\saida" });

            Assert.True(r.Success);
            Assert.Equal(RunnerMode.Sweep, r.Args.Mode);
        }

        [Fact]
        public void Posicional_com_argumentos_de_menos_mantem_exit_2()
        {
            var r = RunnerArgsParser.Parse(new[] { @"C:\gf", "PAC_1", "MAP_9", @"C:\in.txt" });

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.UsageError, r.ExitCode);
            Assert.NotEmpty(r.Messages);
        }

        [Fact]
        public void SWEEP_com_argumentos_de_menos_mantem_exit_2()
        {
            var r = RunnerArgsParser.Parse(new[] { "SWEEP", @"C:\gf", "PAC_1", "MAP_9", @"C:\examples" });

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.UsageError, r.ExitCode);
        }

        [Fact]
        public void Sem_argumento_nenhum_mantem_exit_2()
        {
            var r = RunnerArgsParser.Parse(new string[0]);

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.UsageError, r.ExitCode);
        }

        // ─────────────────── o que o parser não reconhece tem que ESTOURAR ───────────────────

        /// <summary>
        /// Coração do requisito: argumento desconhecido tem exit code PRÓPRIO (7), separado do
        /// uso posicional errado (2). Sem isso, "a API fala um protocolo que o runner não entende"
        /// se disfarça de qualquer outra falha — foi assim que o defeito virou "exit=4".
        /// </summary>
        [Fact]
        public void Argumento_desconhecido_falha_com_exit_proprio_e_nomeia_a_flag()
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--globalFolder", @"C:\gf",
                "--turbo",        "sim",
                "--inputFile",    @"C:\in.txt",
                "--outputFile",   @"C:\out.xml",
                "--mapperId",     "MAP_1"
            });

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.InvalidNamedArgument, r.ExitCode);
            Assert.NotEqual(RunnerExitCodes.UsageError, r.ExitCode);
            Assert.Contains(r.Messages, m => m.Contains("--turbo"));
            Assert.Null(r.Args);
        }

        /// <summary>
        /// A flag desconhecida não pode deslocar o restante: o erro tem que ser SÓ sobre ela, não
        /// uma cascata que esconde a causa (e muito menos um parse "bem-sucedido" torto).
        /// </summary>
        [Fact]
        public void Argumento_desconhecido_nao_desloca_os_seguintes()
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--turbo",        "sim",
                "--globalFolder", @"C:\gf",
                "--inputFile",    @"C:\in.txt",
                "--outputFile",   @"C:\out.xml",
                "--mapperId",     "MAP_1"
            });

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.InvalidNamedArgument, r.ExitCode);

            // Nenhuma queixa sobre obrigatório ausente: os demais foram lidos corretamente, o único
            // problema é a flag desconhecida.
            Assert.DoesNotContain(r.Messages, m => m.Contains("--globalFolder é obrigatório"));
            Assert.DoesNotContain(r.Messages, m => m.Contains("--inputFile é obrigatório"));
            Assert.DoesNotContain(r.Messages, m => m.Contains("Informe --mapperId"));
        }

        /// <summary>
        /// Flag sem valor não pode consumir a flag seguinte — seria o mesmo deslocamento por outro
        /// caminho (--globalFolder ficaria com "--inputFile" como valor).
        /// </summary>
        [Fact]
        public void Flag_sem_valor_nao_engole_a_flag_seguinte()
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--globalFolder",
                "--inputFile",  @"C:\in.txt",
                "--outputFile", @"C:\out.xml",
                "--mapperId",   "MAP_1"
            });

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.InvalidNamedArgument, r.ExitCode);
            Assert.Contains(r.Messages, m => m.Contains("Valor ausente para --globalFolder"));
        }

        [Fact]
        public void Flag_no_fim_do_vetor_sem_valor_falha()
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--globalFolder", @"C:\gf",
                "--inputFile",    @"C:\in.txt",
                "--outputFile",   @"C:\out.xml",
                "--mapperId",     "MAP_1",
                "--fileName"
            });

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.InvalidNamedArgument, r.ExitCode);
            Assert.Contains(r.Messages, m => m.Contains("Valor ausente para --fileName"));
        }

        /// <summary>
        /// Token solto no meio da forma nomeada é erro — jamais um posicional implícito. Aceitar
        /// mistura é o que reabriria a porta do deslocamento.
        /// </summary>
        [Fact]
        public void Token_solto_na_forma_nomeada_nao_vira_posicional()
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--globalFolder", @"C:\gf",
                @"C:\solto.txt",
                "--inputFile",    @"C:\in.txt",
                "--outputFile",   @"C:\out.xml",
                "--mapperId",     "MAP_1"
            });

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.InvalidNamedArgument, r.ExitCode);
            Assert.Contains(r.Messages, m => m.Contains("Argumento inesperado"));
        }

        [Theory]
        [InlineData("--globalFolder")]
        [InlineData("--inputFile")]
        [InlineData("--outputFile")]
        public void Obrigatorio_ausente_na_forma_nomeada_falha_explicitamente(string flagOmitida)
        {
            var completo = new[]
            {
                "--globalFolder", @"C:\gf",
                "--inputFile",    @"C:\in.txt",
                "--outputFile",   @"C:\out.xml",
                "--mapperId",     "MAP_1"
            };

            var reduzido = new List<string>();
            for (int i = 0; i < completo.Length; i += 2)
            {
                if (string.Equals(completo[i], flagOmitida, StringComparison.OrdinalIgnoreCase))
                    continue;
                reduzido.Add(completo[i]);
                reduzido.Add(completo[i + 1]);
            }

            var r = RunnerArgsParser.Parse(reduzido.ToArray());

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.InvalidNamedArgument, r.ExitCode);
            Assert.Contains(r.Messages, m => m.Contains(flagOmitida + " é obrigatório"));
        }

        [Fact]
        public void Sem_mapperId_e_sem_mapperName_falha()
        {
            var r = RunnerArgsParser.Parse(new[]
            {
                "--globalFolder", @"C:\gf",
                "--inputFile",    @"C:\in.txt",
                "--outputFile",   @"C:\out.xml"
            });

            Assert.False(r.Success);
            Assert.Equal(RunnerExitCodes.InvalidNamedArgument, r.ExitCode);
            Assert.Contains(r.Messages, m => m.Contains("--mapperId") && m.Contains("--mapperName"));
        }

        /// <summary>
        /// Exit codes são o contrato de diagnóstico com a API (que só loga o número quando o stderr
        /// se perde). Colisão entre eles apaga a distinção que motivou este trabalho.
        /// </summary>
        [Fact]
        public void Exit_codes_novos_nao_colidem_com_os_existentes()
        {
            var codigos = new[]
            {
                RunnerExitCodes.Ok,
                RunnerExitCodes.Fatal,
                RunnerExitCodes.UsageError,
                RunnerExitCodes.BootstrapFailed,
                RunnerExitCodes.InputNotFound,
                RunnerExitCodes.EmptyResult,
                RunnerExitCodes.SweepAllFailed,
                RunnerExitCodes.InvalidNamedArgument,
                RunnerExitCodes.MapperNameUnresolved
            };

            Assert.Equal(codigos.Length, codigos.Distinct().Count());
        }
    }
}
