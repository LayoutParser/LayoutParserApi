let txtContent = '';
let parsedResult = null;
let currentHighlight = null;
let indentationEnabled = true; // Nova variável para controlar indentação

window.switchInternalTab = function (tabName) {
    // Esconde todas as tabs internas
    document.querySelectorAll('.internal-tab-content').forEach(tab => {
        tab.classList.remove('active');
    });
    document.querySelectorAll('.internal-tab-button').forEach(button => {
        button.classList.remove('active');
    });

    // Mostra a tab interna selecionada
    document.getElementById(tabName + 'TabInternal').classList.add('active');
    event.target.classList.add('active');

    addConsoleMessage('info', `Navegando para: ${tabName === 'structure' ? 'Estrutura do Layout' : 'Conteúdo do Arquivo'}`);
};

window.toggleWrap = function () {
    const txtDisplay = document.getElementById('txtDisplay');
    if (txtDisplay.classList.contains('nowrap')) {
        txtDisplay.classList.remove('nowrap');
        addConsoleMessage('info', 'Quebra de linha ativada');
    } else {
        txtDisplay.classList.add('nowrap');
        addConsoleMessage('info', 'Quebra de linha desativada - Scroll horizontal');
    }
};

// Sistema de Console
class ProcessingConsole {
    constructor() {
        this.messages = [];
        this.maxMessages = 100;
    }

    addMessage(type, message) {
        const timestamp = new Date().toLocaleTimeString();
        const messageObj = { type, message, timestamp };

        this.messages.push(messageObj);

        if (this.messages.length > this.maxMessages) {
            this.messages.shift();
        }

        this.updateConsoleDisplay();
        this.updateQuickStatus(type, message);
    }

    updateConsoleDisplay() {
        const consoleElement = document.getElementById('processingConsole');
        const consoleInfo = document.getElementById('consoleInfo');

        consoleElement.innerHTML = this.messages.map(msg => `
      <div class="console-message ${msg.type}">
        <span class="timestamp">[${msg.timestamp}]</span>
        ${this.getIcon(msg.type)} ${msg.message}
      </div>
    `).join('');

        consoleInfo.textContent = `${this.messages.length} mensagens`;
        consoleElement.scrollTop = consoleElement.scrollHeight;
    }

    updateQuickStatus(type, message) {
        const quickStatus = document.getElementById('quickStatus');
        quickStatus.textContent = `[${new Date().toLocaleTimeString()}] ${message}`;
        quickStatus.className = `console-message ${type}`;
    }

    getIcon(type) {
        const icons = {
            'info': 'ℹ️',
            'success': '✅',
            'warning': '⚠️',
            'error': '❌'
        };
        return icons[type] || '📝';
    }

    clear() {
        this.messages = [];
        this.updateConsoleDisplay();
        this.updateQuickStatus('info', 'Console limpo');
    }

    exportLog() {
        const logContent = this.messages.map(msg =>
            `[${msg.timestamp}] ${msg.type.toUpperCase()}: ${msg.message}`
        ).join('\n');

        const blob = new Blob([logContent], { type: 'text/plain' });
        const url = URL.createObjectURL(blob);

        const a = document.createElement('a');
        a.href = url;
        a.download = `processamento_log_${new Date().toISOString().split('T')[0]}.txt`;
        a.click();

        URL.revokeObjectURL(url);
    }
}

// Instância global do console
window.processingConsole = new ProcessingConsole();

// Funções auxiliares para o console
window.addConsoleMessage = function (type, message) {
    window.processingConsole.addMessage(type, message);
};

window.clearConsole = function () {
    window.processingConsole.clear();
};

window.exportConsole = function () {
    window.processingConsole.exportLog();
};

// Sistema de Abas Principais
window.switchMainTab = function (tabName) {
    // Esconde todas as tabs
    document.querySelectorAll('.main-tab-content').forEach(tab => {
        tab.classList.remove('active');
    });
    document.querySelectorAll('.main-tab-button').forEach(button => {
        button.classList.remove('active');
    });

    // Mostra a tab selecionada
    document.getElementById(tabName + 'Tab').classList.add('active');

    // ✅ CORREÇÃO: Usar event apenas se existir
    if (event && event.target) {
        event.target.classList.add('active');
    } else {
        // Fallback: encontrar o botão correto
        const button = document.querySelector(`.main-tab-button[onclick*="${tabName}"]`);
        if (button) button.classList.add('active');
    }

    addConsoleMessage('info', `📂 Navegando para aba: ${tabName}`);
};

// Sistema de Sub-Abas (Upload vs Geração)
window.switchSubTab = function (tabName) {
    // Esconde todas as sub-tabs
    document.querySelectorAll('.sub-tab-content').forEach(tab => {
        tab.classList.remove('active');
    });
    document.querySelectorAll('.sub-tab-button').forEach(button => {
        button.classList.remove('active');
    });

    // Mostra a sub-tab selecionada
    document.getElementById(tabName + 'SubTab').classList.add('active');

    // Marca o botão como ativo
    if (event && event.target) {
        event.target.classList.add('active');
    } else {
        const button = document.querySelector(`.sub-tab-button[onclick*="${tabName}"]`);
        if (button) button.classList.add('active');
    }

    const tabLabel = tabName === 'upload' ? 'Upload e Processamento' : 'Geração de Dados Sintéticos';
    addConsoleMessage('info', `🔄 Modo selecionado: ${tabLabel}`);
};

// Variáveis globais


// COMUNICAÇÃO COM A API - FUNÇÃO PRINCIPAL DE UPLOAD
async function handleFileUpload(e) {
    e.preventDefault();

    const txtFile = document.getElementById("txtFile").files[0];
    const statusDiv = document.getElementById("uploadStatus");

    // Verificar se há um layout selecionado do banco
    if (!selectedLayoutFromDatabaseForUpload) {
        statusDiv.innerHTML = '<span style="color: red;">❌ Selecione um layout do banco de dados primeiro</span>';
        addConsoleMessage('error', 'Erro: Selecione um layout do banco de dados primeiro');
        return;
    }

    if (!txtFile) {
        statusDiv.innerHTML = '<span style="color: red;">❌ Selecione o arquivo TXT</span>';
        addConsoleMessage('error', 'Erro: Selecione o arquivo TXT');
        return;
    }

    statusDiv.innerHTML = 'Processando...';
    addConsoleMessage('info', 'Iniciando processamento de arquivos...');

    // Ler conteúdo do TXT para detecção automática
    const txtReader = new FileReader();
    txtReader.onload = async function (e) {
        txtContent = e.target.result;

        // Não fazemos mais o split - usamos o lineSequence do JSON para localizar as linhas
        addConsoleMessage('info', `📊 Arquivo TXT carregado: ${txtContent.length} caracteres`);

        // DETECÇÃO AUTOMÁTICA DO TIPO DE LAYOUT
        const layoutType = LayoutDetector.detectLayoutType(txtContent);
        const layoutConfig = LayoutDetector.getLayoutConfig(layoutType, txtContent);

        addConsoleMessage('success', `📋 Tipo detectado: ${layoutConfig.name}`);
        addConsoleMessage('info', `📊 Configuração: ${layoutConfig.splitMethod === 'fixed' ? 'Linhas fixas' : 'Linhas variáveis'}`);

        if (layoutType === 'idoc') {
            const idocStructure = LayoutDetector.analyzeIDocStructure(txtContent);
            addConsoleMessage('info', `📑 Segmentos iDoc encontrados: ${Object.keys(idocStructure).length}`);
        }

        // renderTxt(layoutConfig); // Desabilitado - usando fieldViewer
        updateTextStats(layoutConfig);

        // Enviar para API com informações do tipo detectado
        await sendToAPI(selectedLayoutFromDatabaseForUpload, txtFile, layoutType, layoutConfig);
    };
    txtReader.readAsText(txtFile);
}

async function sendToAPI(selectedLayout, txtFile, layoutType, layoutConfig) {
    const formData = new FormData();
    
    // Criar um arquivo virtual com o layout selecionado do banco
    const layoutContent = selectedLayout.decryptedContent || selectedLayout.valueContent;
    const blob = new Blob([layoutContent], { type: 'application/xml' });
    const layoutFile = new File([blob], `${selectedLayout.name}.xml`, { type: 'application/xml' });
    
    // O backend espera apenas layoutFile e txtFile
    formData.append("layoutFile", layoutFile);
    formData.append("txtFile", txtFile);

    // DEBUG: Verificar o que está sendo enviado
    console.group('📤 DEBUG: Dados sendo enviados');
    console.log('layoutFile:', layoutFile ? `${layoutFile.name} (${layoutFile.size} bytes)` : 'VAZIO');
    console.log('txtFile:', txtFile ? `${txtFile.name} (${txtFile.size} bytes)` : 'VAZIO');
    console.log('layoutType (frontend):', layoutType);
    console.log('layoutConfig (frontend):', layoutConfig);
    
    // Verificar conteúdo do FormData
    console.log('FormData entries:');
    for (let pair of formData.entries()) {
        if (pair[1] instanceof File) {
            console.log(`  ${pair[0]}: File(${pair[1].name}, ${pair[1].size} bytes)`);
        } else {
            console.log(`  ${pair[0]}: ${pair[1]}`);
        }
    }
    console.groupEnd();

    try {
        addConsoleMessage('info', `🔄 Enviando arquivos para API (Tipo: ${layoutType})...`);
        addConsoleMessage('info', `📁 Layout: ${layoutFile.name} (${(layoutFile.size / 1024).toFixed(2)} KB)`);
        addConsoleMessage('info', `📝 TXT: ${txtFile.name} (${(txtFile.size / 1024).toFixed(2)} KB)`);
        addConsoleMessage('info', `📡 URL: http://localhost:5214/api/parse/upload`);

        // Tentar enviar para API diretamente (sem health check)
        addConsoleMessage('info', '🔄 Conectando ao servidor...');

        const resp = await fetch("http://localhost:5214/api/parse/upload", {
            method: "POST",
            body: formData
            // Removemos o header Content-Type - o browser configura automaticamente para multipart/form-data
        });

        addConsoleMessage('info', `📊 Status HTTP: ${resp.status} ${resp.statusText}`);

        if (!resp.ok) {
            const errorText = await resp.text();
            addConsoleMessage('error', `❌ Resposta de erro: ${errorText}`);
            throw new Error(errorText || `HTTP ${resp.status} - ${resp.statusText}`);
        }

        // DEBUG: Ver o que está vindo do servidor
        const contentType = resp.headers.get('content-type');
        addConsoleMessage('info', `📋 Content-Type da resposta: ${contentType}`);
        
        const responseText = await resp.text();
        console.log('📥 Resposta completa (texto):', responseText);
        addConsoleMessage('info', `📥 Tamanho da resposta: ${responseText.length} caracteres`);
        
        // Se a resposta estiver vazia, avisar
        if (!responseText || responseText.trim() === '') {
            addConsoleMessage('error', '❌ API retornou resposta vazia!');
            throw new Error('API retornou resposta vazia (null/empty body)');
        }
        
        // Tentar fazer parse do JSON
        let result;
        try {
            result = JSON.parse(responseText);
            console.log('📥 Resposta parseada (JSON):', result);
        } catch (parseError) {
            addConsoleMessage('error', `❌ Erro ao fazer parse do JSON: ${parseError.message}`);
            addConsoleMessage('error', `📄 Resposta recebida: ${responseText.substring(0, 500)}`);
            throw new Error(`Resposta inválida do servidor: ${parseError.message}`);
        }
        
        addConsoleMessage('success', '✅ API respondeu com sucesso');

        if (result.success) {
            // Salvar resultado global
            parsedResult = result;
            parsedResult.layoutType = layoutType;
            parsedResult.layoutConfig = layoutConfig;
            
            // Salvar também o layout original do backend
            window.parsedResult = result;
            
            addConsoleMessage('info', `🔍 Tipo detectado pela API: ${result.detectedType || 'N/A'}`);
            addConsoleMessage('info', `📊 Campos encontrados: ${result.fields ? result.fields.length : 0}`);
            
            // Renderizar árvore e sumário
            if (result.fields && result.fields.length > 0) {
                renderTree(result.fields, result.documentStructure, layoutConfig);
            } else {
                addConsoleMessage('warning', '⚠️ Nenhum campo foi encontrado no documento');
            }
            
            if (result.summary) {
                renderSummary(result.summary, layoutConfig);
            } else {
                addConsoleMessage('warning', '⚠️ Sumário não disponível');
            }

            const statusDiv = document.getElementById("uploadStatus");
            statusDiv.innerHTML = '<span style="color: green;">✅ Processamento concluído!</span>';
            addConsoleMessage('success', `✅ Processamento ${layoutConfig.name} concluído!`);

            // Renderizar visualizador integrado
            renderIntegratedViewer();

            // Mudar para aba de análise
            switchMainTab('analysis');
        } else {
            throw new Error(result.error || result.errorMessage || 'Erro no processamento');
        }
    } catch (error) {
        console.error("❌ Erro detalhado:", error);
        
        // Verificar tipo de erro
        let errorMsg = '';
        let troubleshootingSteps = [];
        
        if (error.name === 'TypeError' && (error.message.includes('fetch') || error.message.includes('Failed to fetch'))) {
            errorMsg = '❌ Não foi possível conectar ao servidor';
            troubleshootingSteps = [
                '1. Verifique se o servidor está rodando: Execute "dotnet run" no terminal',
                '2. Confirme que está na porta 5214: http://localhost:5214',
                '3. Verifique se não há firewall bloqueando a conexão',
                '4. Tente acessar http://localhost:5214 diretamente no navegador'
            ];
            addConsoleMessage('error', '🔴 Servidor não está respondendo');
            addConsoleMessage('warning', '💡 Execute "dotnet run" no terminal do projeto');
        } else if (error.name === 'TimeoutError') {
            errorMsg = '❌ Timeout: O servidor demorou muito para responder';
            troubleshootingSteps = [
                '1. O servidor pode estar sobrecarregado',
                '2. Execute "dotnet run" se o servidor não estiver rodando',
                '3. Verifique os logs do servidor no terminal'
            ];
            addConsoleMessage('error', '🔴 Timeout na conexão');
            addConsoleMessage('warning', '💡 Servidor pode não estar rodando');
        } else if (error.message.includes('CORS')) {
            errorMsg = '❌ Erro CORS: Política de segurança bloqueou a requisição';
            troubleshootingSteps = [
                '1. Configure CORS no Program.cs',
                '2. Adicione: app.UseCors() após app.UseRouting()',
                '3. Reinicie o servidor após alterações'
            ];
            addConsoleMessage('error', '🔴 Erro de CORS detectado');
            addConsoleMessage('warning', '💡 Configure CORS no backend');
        } else {
            errorMsg = `❌ Erro: ${error.message}`;
            troubleshootingSteps = [
                '1. Verifique se o servidor está rodando',
                '2. Confira os logs do servidor',
                '3. Tente recarregar a página (F5)'
            ];
            addConsoleMessage('error', errorMsg);
        }
        
        // Adicionar troubleshooting no console
        troubleshootingSteps.forEach((step, i) => {
            addConsoleMessage('warning', `   ${step}`);
        });
        
        const statusDiv = document.getElementById("uploadStatus");
        statusDiv.innerHTML = `
            <div style="color: red; padding: 15px; background: #ffebee; border-left: 4px solid #d32f2f; border-radius: 4px;">
                <strong>${errorMsg}</strong><br><br>
                <strong>Como resolver:</strong>
                <ol style="margin: 10px 0; padding-left: 20px;">
                    ${troubleshootingSteps.map(step => `<li style="margin: 5px 0;">${step.replace(/^\d+\.\s*/, '')}</li>`).join('')}
                </ol>
                <br>
                <strong>Comando para iniciar o servidor:</strong><br>
                <code style="background: #333; color: #0f0; padding: 5px 10px; display: inline-block; border-radius: 3px; margin-top: 5px;">
                    dotnet run
                </code>
            </div>
        `;
    }
}

// Função para habilitar/desabilitar análise IA
window.enableIAnalysis = function (enable) {
    const iaButton = document.querySelector('button[onclick="highlightProblems()"]');
    if (iaButton) {
        iaButton.disabled = !enable;
        if (enable) {
            iaButton.innerHTML = '⭐ Mostrar Problemas IA';
            addConsoleMessage('info', 'Análise IA habilitada - Clique no botão para ver problemas');
        }
    }
};

// ADICIONAR ESTA FUNÇÃO NO main.js (após a função renderSummary)

function renderValidationAlerts(validation) {
    const summaryDiv = document.getElementById('summary');

    let alertHTML = '';

    // ✅ ERROS CRÍTICOS
    if (validation.criticalErrors && validation.criticalErrors.length > 0) {
        alertHTML += `
        <div style="background: #f8d7da; border: 1px solid #f5c6cb; padding: 10px; margin: 10px 0; border-radius: 4px;">
            <strong>🔴 Erros Críticos:</strong>
            <ul style="margin: 5px 0; padding-left: 20px;">
                ${validation.criticalErrors.map(error => `<li>${error}</li>`).join('')}
            </ul>
        </div>
    `;
        addConsoleMessage('error', `Encontrados ${validation.criticalErrors.length} erro(s) crítico(s)`);
    }

    // ✅ LINHAS OBRIGATÓRIAS AUSENTES
    if (validation.missingRequiredLines && validation.missingRequiredLines.length > 0) {
        alertHTML += `
        <div style="background: #fff3cd; border: 1px solid #ffeaa7; padding: 10px; margin: 10px 0; border-radius: 4px;">
            <strong>⚠ Estrutura Incompleta:</strong>
            <ul style="margin: 5px 0; padding-left: 20px;">
                ${validation.missingRequiredLines.map(line => `<li>Linha obrigatória ausente: <strong>${line}</strong></li>`).join('')}
            </ul>
        </div>
    `;
        addConsoleMessage('warning', `${validation.missingRequiredLines.length} linha(s) obrigatória(s) ausente(s)`);
    }

    // ✅ SUGESTÕES DA VALIDAÇÃO
    if (validation.suggestions && validation.suggestions.length > 0) {
        alertHTML += `
        <div style="background: #d1ecf1; border: 1px solid #bee5eb; padding: 10px; margin: 10px 0; border-radius: 4px;">
            <strong>💡 Sugestões:</strong>
            <ul style="margin: 5px 0; padding-left: 20px;">
                ${validation.suggestions.map(suggestion => `<li>${suggestion.message}</li>`).join('')}
            </ul>
        </div>
    `;
        addConsoleMessage('info', `${validation.suggestions.length} sugestão(ões) de melhoria`);
    }

    // ✅ ADICIONAR AO SUMMARY (não substituir)
    if (alertHTML) {
        const existingSummary = summaryDiv.innerHTML;
        summaryDiv.innerHTML = alertHTML + existingSummary;
    }
}

// FUNÇÃO AUXILIAR PARA DETECTAR LINHAS DO DOCUMENTO (se necessário)
function detectDocumentLines(content) {
    addConsoleMessage('info', 'Detectando estrutura do documento...');

    const lines = content.split('\n');
    const structure = {
        lines: {},
        validation: {
            criticalErrors: [],
            missingRequiredLines: [],
            suggestions: []
        }
    };

    // Exemplo básico de detecção - ajuste conforme sua necessidade
    lines.forEach((line, index) => {
        if (line.startsWith('HEADER')) {
            structure.lines.HEADER = structure.lines.HEADER || [];
            structure.lines.HEADER.push({
                content: line,
                lineNumber: index,
                positions: extractPositions(line),
                validation: validateHeaderLine(line)
            });
        } else if (line.startsWith('000')) {
            structure.lines.LINHA000 = structure.lines.LINHA000 || [];
            structure.lines.LINHA000.push({
                content: line,
                lineNumber: index,
                positions: extractPositions(line),
                validation: validateLinha000(line)
            });
        }
        // Adicione mais condições conforme seu layout específico
    });

    return structure;
}

// FUNÇÕES AUXILIARES PARA VALIDAÇÃO (exemplos)
function extractPositions(line) {
    const positions = [];
    // Implemente a extração de posições conforme seu layout
    for (let i = 0; i < line.length; i += 10) {
        positions.push({
            start: i,
            end: i + 9,
            value: line.substring(i, i + 10),
            type: detectFieldType(line.substring(i, i + 10))
        });
    }
    return positions;
}

function detectFieldType(value) {
    // Implemente a detecção do tipo de campo
    if (/^\d+$/.test(value)) return 'numeric';
    if (/^[A-Za-z\s]+$/.test(value)) return 'text';
    return 'mixed';
}

function validateHeaderLine(line) {
    const validation = {
        status: 'valid',
        message: ''
    };

    // Exemplo de validações para HEADER
    if (line.length < 50) {
        validation.status = 'warning';
        validation.message = 'Linha HEADER muito curta';
    }

    return validation;
}

function validateLinha000(line) {
    const validation = {
        status: 'valid',
        message: ''
    };

    // Exemplo de validações para LINHA000
    if (!line.includes('LAYOUT')) {
        validation.status = 'warning';
        validation.message = 'Linha 000 pode estar faltando identificador de layout';
    }

    return validation;
}

// Renderizar árvore (mantida da versão anterior)
function renderTree(fields, documentStructure) {
    const ul = document.getElementById("layoutTree");
    ul.innerHTML = '';

    if (documentStructure && documentStructure.validation) {
        renderValidationAlerts(documentStructure.validation);
    }

    // ✅ DEFINIR groupedByLine DENTRO DA FUNÇÃO
    const groupedByLine = fields.reduce((acc, field) => {
        if (!acc[field.lineName]) {
            acc[field.lineName] = [];
        }
        acc[field.lineName].push(field);
        return acc;
    }, {});

    // Remover botões de controle existentes (se houver)
    const existingButtons = ul.parentNode.querySelectorAll('.tree-control-buttons');
    existingButtons.forEach(btn => btn.remove());

    // Criar container para os botões
    const buttonContainer = document.createElement('div');
    buttonContainer.className = 'tree-control-buttons';
    buttonContainer.style.marginBottom = '10px';
    
    // Adicionar botão Recolher Tudo
    const foldAllButton = document.createElement('button');
    foldAllButton.textContent = '📂 Recolher Tudo';
    foldAllButton.style.padding = '5px 10px';
    foldAllButton.style.fontSize = '12px';
    foldAllButton.style.marginRight = '10px';
    foldAllButton.onclick = () => foldAllLines(true);
    buttonContainer.appendChild(foldAllButton);

    // Adicionar botão Expandir Tudo
    const expandAllButton = document.createElement('button');
    expandAllButton.textContent = '📁 Expandir Tudo';
    expandAllButton.style.padding = '5px 10px';
    expandAllButton.style.fontSize = '12px';
    expandAllButton.onclick = () => foldAllLines(false);
    buttonContainer.appendChild(expandAllButton);

    // Inserir container antes da lista
    ul.parentNode.insertBefore(buttonContainer, ul);

    // Renderizar cada linha
    Object.keys(groupedByLine).forEach(lineName => {
        const lineLi = document.createElement('li');

        lineLi.innerHTML = `
            <div class="tree-line-header" data-line="${lineName}">
                <span class="toggle-icon">▶</span>
                <strong>${lineName}</strong>
                <span class="field-count">(${groupedByLine[lineName].length} campos)</span>
            </div>
        `;
        lineLi.className = 'tree-line';

        const lineUl = document.createElement('ul');
        lineUl.className = 'tree-line-content';
        lineUl.style.display = 'none';

        // Renderizar cada campo da linha
        groupedByLine[lineName].forEach(field => {
            const fieldLi = document.createElement('li');
            fieldLi.innerHTML = `
        <div class="field-info">
            <strong>${field.fieldName}</strong> (Seq: ${field.sequence})
            <div>📍 Posição na linha: ${field.start}-${field.start + field.length - 1}</div>
            <div>📝 Valor: "${field.value}"</div>
            <div>📋 Tipo: ${field.dataType || 'String'} | Obrigatório: ${field.isRequired ? 'Sim' : 'Não'}</div>
            <div>📊 Status: <span class="status-${field.status}">${field.status}</span></div>
            <div style="font-size: 10px; color: #666; background: #e3f2fd; padding: 4px; border-radius: 3px; margin-top: 4px;">
                🏷️ ${field.lineName} | 🎯 Start: ${field.start} | 📏 Length: ${field.length}
            </div>
        </div>
    `;
            fieldLi.className = `tree-item status-${field.status}`;
            fieldLi.setAttribute('data-line', field.lineName);
            fieldLi.setAttribute('data-start', field.start);
            fieldLi.setAttribute('data-length', field.length);

            fieldLi.addEventListener('click', () => {
                console.log('🖱️ Campo clicado:', {
                    fieldName: field.fieldName,
                    lineName: field.lineName,
                    start: field.start,
                    length: field.length,
                    value: field.value
                });

                // Renderizar a linha no visualizador se ainda não estiver renderizada
                if (!document.querySelector(`#fieldDisplay .Field[title*="${field.fieldName}"]`)) {
                    renderFieldsForLine(field.lineName, groupedByLine[field.lineName]);
                }

                // Destacar o campo no visualizador integrado
                highlightFieldInViewer(field);
            });

            lineUl.appendChild(fieldLi);
        });

        const header = lineLi.querySelector('.tree-line-header');
        header.addEventListener('click', (e) => {
            if (!e.target.classList.contains('tree-line-header') && !e.target.closest('.tree-line-header')) return;
            toggleTreeLine(lineName);
            // Renderizar campos no visualizador
            renderFieldsForLine(lineName, groupedByLine[lineName]);
        });

        lineLi.appendChild(lineUl);
        ul.appendChild(lineLi);
    });

    setupSearch();
    addConsoleMessage('success', `Árvore de layout renderizada com ${Object.keys(groupedByLine).length} linhas`);
}

window.highlightProblems = function () {
    if (!window.parsedResult || !window.txtContent) {
        addConsoleMessage('error', 'Nenhum documento processado para análise');
        return;
    }

    const issues = window.iaStarSystem.analyzeForStars(window.parsedResult, window.txtContent);
    window.iaStarSystem.addStarsToTree();

    addConsoleMessage('success', `IA identificou ${issues.length} campos com problemas`);
    updateProcessingStats(issues);
};

window.exportAnalysis = function () {
    addConsoleMessage('info', 'Exportando análise...');
    // Implementar export de análise
};


// Renderizar sumário
function renderSummary(summary) {
    const summaryDiv = document.getElementById("summary");
    summaryDiv.innerHTML = `
<div style="font-size: 12px; background: #f0f0f0; padding: 8px; border-radius: 4px;">
  <strong>Resumo do Documento:</strong><br>
  📄 Tipo: ${summary.documentType} | Versão: ${summary.layoutVersion}<br>
  📊 Linhas: ${summary.presentLines}/${summary.expectedLines} | 
  Campos: ${summary.totalFields}<br>
  <span style="color: green;">✓ Válidos: ${summary.validFields} (${summary.complianceRate.toFixed(1)}%)</span> | 
  <span style="color: orange;">⚠ Alertas: ${summary.warningFields}</span> | 
  <span style="color: red;">✗ Erros: ${summary.errorFields}</span>
  ${summary.missingLines > 0 ?
            `<br><span style="color: red;">🔴 Linhas ausentes: ${summary.missingLines}</span>` : ''
        }
</div>
`;

    // Atualizar estatísticas no console
    updateProcessingStatsFromSummary(summary);
}

function updateProcessingStatsFromSummary(summary) {
    document.getElementById('statTotalFields').textContent = summary.totalFields;
    document.getElementById('statValidFields').textContent = summary.validFields;
    document.getElementById('statWarningFields').textContent = summary.warningFields;
    document.getElementById('statErrorFields').textContent = summary.errorFields;
    document.getElementById('statCompliance').textContent = summary.complianceRate.toFixed(0) + '%';
}

// Funções de highlight (mantidas da versão anterior)
function highlightField(globalStart, length, lineName = '') {
    console.group('🎯 HighlightField CORRETO');

    const lineLength = 600;
    const lineIndex = Math.floor(globalStart / lineLength);
    const positionInLine = globalStart % lineLength;

    console.log('📥 Parâmetros:', { globalStart, length, lineName });
    console.log('📊 Cálculo:', {
        lineIndex,
        positionInLine,
        linhaDisplay: lineIndex + 1,
        linhaEsperada: lineName
    });

    currentHighlight = {
        lineIndex: lineIndex,
        start: positionInLine,
        length,
        globalStart,
        lineName
    };

    // renderTxt(); // Desabilitado - usando fieldViewer
    addConsoleMessage('success', `✅ Campo selecionado: ${lineName} (Linha ${lineIndex + 1})`);
    console.groupEnd();
}

function debugFieldSelection(field) {
    console.group('🔍 Debug Seleção Campo');
    console.log('Campo:', field.fieldName);
    console.log('Linha:', field.lineName);
    console.log('Start (base 1):', field.start);
    console.log('Length:', field.length);

    const globalStart = field.start - 1; // Converter para base 0
    const lineIndex = Math.floor(globalStart / 600);
    const positionInLine = globalStart % 600;

    console.log('Global Start (base 0):', globalStart);
    console.log('Linha Index:', lineIndex);
    console.log('Posição na Linha:', positionInLine);
    console.log('Linha Real:', lineIndex + 1);

    // ✅ VERIFICAR SE O CÁLCULO ESTÁ CORRETO
    const expectedLine = getExpectedLineNumber(field.lineName);
    console.log('Linha Esperada:', expectedLine);
    console.log('Cálculo Correto?', lineIndex + 1 === expectedLine);
    console.groupEnd();

    return globalStart;
}

// ✅ FUNÇÃO PARA OBTER NÚMERO DA LINHA ESPERADA
function getExpectedLineNumber(lineName) {
    const lineOrder = {
        'HEADER': 1,
        'LINHA000': 2,
        'LINHA001': 3,
        'LINHA002': 4,
        'LINHA003': 5,
        'LINHA004': 6,
        'LINHA005': 7,
        'LINHA006': 8,
        'LINHA007': 9,
        'LINHA008': 10,
        'LINHA009': 11,
        'LINHA010': 12,
        'LINHA011': 13,
        'LINHA012': 14,
        'LINHA013': 15,
        'LINHA014': 16,
        'LINHA015': 17,
        'LINHA016': 18,
        'LINHA017': 19,
        'LINHA018': 20,
        'LINHA019': 21,
        'LINHA020': 22,
        'LINHA050': 23,
        'LINHA051': 24,
        'LINHA052': 25,
        'LINHA053': 26
    };

    return lineOrder[lineName] || 1;
}

function renderTxt(layoutConfig = null) {
    let content = txtContent || '';
    const lines = [];

    if (layoutConfig && layoutConfig.splitMethod === 'fixed') {
        // MQSeries: dividir em linhas fixas de 600 caracteres
        const lineLength = layoutConfig.lineLength || 600;
        for (let i = 0; i < content.length; i += lineLength) {
            let line = content.slice(i, i + lineLength);
            if (line.length < lineLength) {
                line = line.padEnd(lineLength, ' ');
            }
            lines.push(line);
        }
    } else {
        // iDoc ou outro: usar quebras de linha naturais
        lines.push(...content.split('\n'));
    }

    let highlightedContent = '';

    lines.forEach((line, lineIndex) => {
        const lineNumber = lineIndex + 1;

        // Identificar tipo de linha baseado no conteúdo
        let lineType = identifyLineType(line, layoutConfig);
        let linePrefix = extractLinePrefix(line, layoutConfig);

        const isHighlighted = currentHighlight && currentHighlight.lineIndex === lineIndex;

        highlightedContent += renderLine(line, lineNumber, lineType, linePrefix, isHighlighted, layoutConfig);
    });

    document.getElementById("txtDisplay").innerHTML = highlightedContent;

    if (currentHighlight) {
        setTimeout(() => {
            scrollToHighlightedLine();
        }, 100);
    }
}

function renderLine(line, lineNumber, lineType, linePrefix, isHighlighted, layoutConfig) {
    let lineContent = '';
    
    if (isHighlighted) {
        const start = currentHighlight.start;
        const length = currentHighlight.length;
        
        const before = line.substring(0, start);
        const highlighted = line.substring(start, start + length);
        const after = line.substring(start + length);
        
        lineContent = `${escapeHtml(before)}<span class="highlight">${escapeHtml(highlighted)}</span>${escapeHtml(after)}`;
    } else {
        lineContent = escapeHtml(line);
    }
    
    // Calcular nível de indentação baseado no tipo de linha
    const indentLevel = calculateIndentLevel(lineType, linePrefix);
    const indentClass = indentationEnabled ? `indent-level-${indentLevel}` : '';
    const indentIndicator = indentationEnabled && indentLevel > 0 ? '├─ '.repeat(indentLevel) : '';
    
    return `
        <div class="line-container ${isHighlighted ? 'has-highlight' : ''} ${indentClass}" data-line-type="${lineType}">
            <span class="line-number">${lineNumber}</span>
            <span class="line-type-badge ${lineType}">${linePrefix}</span>
            ${indentIndicator ? `<span class="indent-indicator">${indentIndicator}</span>` : ''}
            <span class="line-content">${lineContent}</span>
        </div>
    `;
}

// Nova função para calcular nível de indentação
function calculateIndentLevel(lineType, linePrefix) {
    if (!indentationEnabled) return 0;
    
    // HEADER sem indentação
    if (lineType === 'HEADER') return 0;
    
    // Extrair número do tipo de linha (LINHA000 -> 000)
    const match = lineType.match(/\d+$/);
    if (match) {
        const num = parseInt(match[0]);
        
        // 000 - Nível 1
        if (num === 0) return 1;
        
        // 001-010 - Nível 2
        if (num >= 1 && num <= 10) return 2;
        
        // 011-050 - Nível 3
        if (num >= 11 && num <= 50) return 3;
        
        // 051-100 - Nível 4  
        if (num >= 51 && num <= 100) return 4;
        
        // 999 - Sem indentação (linha final)
        if (num >= 999) return 0;
        
        // Outras - Nível 2
        return 2;
    }
    
    return 0;
}

function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

function identifyLineType(line, layoutConfig) {
    if (!layoutConfig || layoutConfig.name === 'MQSeries') {
        // Lógica original para MQSeries
        if (line.startsWith('HEADER')) return 'HEADER';
        if (line.startsWith('000')) return 'LINHA000';
        if (line.startsWith('001')) return 'LINHA001';
        // ... resto da lógica original
    } else if (layoutConfig.name === 'iDoc') {
        // Lógica para iDoc
        if (line.startsWith('EDI_DC40')) return 'EDI_DC40';
        if (line.includes('_IDE')) return 'IDE';
        if (line.includes('_EMIT')) return 'EMIT';
        if (line.includes('_DET')) return 'DET';
        if (line.includes('_PROD')) return 'PROD';
        if (line.includes('_TOTAL')) return 'TOTAL';

        // Extrai o segmento completo
        const segmentMatch = line.match(/^(\w+_\w+_\d+_\w+)/);
        return segmentMatch ? segmentMatch[1] : 'OUTROS';
    }

    return 'OUTROS';
}

function extractLinePrefix(line, layoutConfig) {
    if (layoutConfig && layoutConfig.name === 'iDoc') {
        const segmentMatch = line.match(/^(\w+_\w+_\d+_\w+)/);
        return segmentMatch ? segmentMatch[1] : line.substring(0, 30) + '...';
    }

    // Para MQSeries, usar lógica original
    return line.substring(0, Math.min(20, line.length));
}

function scrollToHighlightedLine() {
    if (!currentHighlight) return;

    const lineContainers = document.querySelectorAll('.line-container');
    if (lineContainers[currentHighlight.lineIndex]) {
        lineContainers[currentHighlight.lineIndex].scrollIntoView({
            behavior: 'smooth',
            block: 'center'
        });
    }
}

function updateTextStats() {
    const statsDiv = document.getElementById("textStats");
    statsDiv.innerHTML = `
    <div style="font-size: 11px; color: #666; margin-bottom: 5px;">
      Caracteres: ${txtContent.length} | Linhas: ${txtContent.split('\n').length}
    </div>
  `;
}

function toggleTreeLine(lineName) {
    const header = document.querySelector(`.tree-line-header[data-line="${lineName}"]`);
    if (!header) return;

    const lineElement = header.closest('.tree-line');
    const content = lineElement.querySelector('.tree-line-content');
    const icon = header.querySelector('.toggle-icon');

    if (content && icon) {
        if (content.style.display === 'none' || !content.style.display) {
            content.style.display = 'block';
            icon.textContent = '▼';
        } else {
            content.style.display = 'none';
            icon.textContent = '▶';
        }
    }
}

function foldAllLines(collapse) {
    const headers = document.querySelectorAll('.tree-line-header');

    headers.forEach(header => {
        const content = header.closest('.tree-line').querySelector('.tree-line-content');
        const icon = header.querySelector('.toggle-icon');

        if (collapse) {
            content.style.display = 'none';
            icon.textContent = '▶';
        } else {
            content.style.display = 'block';
            icon.textContent = '▼';
        }
    });
}

function setupSearch() {
    document.getElementById("searchField").addEventListener("input", function (e) {
        const searchTerm = e.target.value.toLowerCase();
        const items = document.getElementsByClassName('tree-item');
        const lineHeaders = document.getElementsByClassName('tree-line-header');

        for (let item of items) {
            const text = item.textContent.toLowerCase();
            if (text.includes(searchTerm)) {
                item.style.display = '';
                const lineHeader = item.closest('.tree-line').querySelector('.tree-line-header');
                const content = item.closest('.tree-line').querySelector('.tree-line-content');
                const icon = lineHeader.querySelector('.toggle-icon');
                content.style.display = 'block';
                icon.textContent = '▼';
            } else {
                item.style.display = 'none';
            }
        }

        for (let header of lineHeaders) {
            const lineContent = header.closest('.tree-line').querySelector('.tree-line-content');
            const visibleItems = lineContent.querySelectorAll('.tree-item[style=""]');
            if (visibleItems.length === 0 && searchTerm !== '') {
                header.style.opacity = '0.5';
            } else {
                header.style.opacity = '1';
            }
        }
    });
}

// Funções auxiliares
function scrollToHighlight() {
    if (currentHighlight) {
        addConsoleMessage('info', 'Navegando para campo selecionado...');
        // Implementar scroll para o highlight
    } else {
        addConsoleMessage('warning', 'Nenhum campo selecionado para navegar');
    }
}

function clearHighlight() {
    currentHighlight = null;
    // renderTxt(); // Desabilitado - usando fieldViewer
    addConsoleMessage('info', 'Seleção de campo limpa');
}

function exportAnalysis() {
    addConsoleMessage('info', 'Exportando análise...');
    // Implementar export de análise
}

// Inicialização
document.addEventListener('DOMContentLoaded', function () {
    addConsoleMessage('success', 'Sistema inicializado com sucesso');

    // Configura event listeners
    document.getElementById("uploadForm").addEventListener("submit", handleFileUpload);

    // Configura busca
    setupSearch();

    // Inicializar sistema de estrelas IA
    window.iaStarSystem = new IAStarSystem();
});

// ATUALIZAÇÃO DE CACHE DO BANCO DE DADOS
async function refreshCacheFromDatabase() {
    const statusDiv = document.getElementById('generationStatus');
    
    try {
        statusDiv.innerHTML = '🔄 Atualizando cache do banco de dados...';
        addConsoleMessage('info', 'Iniciando atualização do cache Redis...');

        const response = await fetch('http://localhost:5214/api/layoutdatabase/refresh-cache', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            }
        });

        if (!response.ok) {
            throw new Error(`Erro ao atualizar cache: ${response.statusText}`);
        }

        const result = await response.json();

        if (result.success) {
            statusDiv.innerHTML = `<span style="color: green;">✅ ${result.message}</span>`;
            addConsoleMessage('success', `Cache atualizado: ${result.message}`);
            addConsoleMessage('info', `Timestamp: ${new Date(result.timestamp).toLocaleString()}`);
        } else {
            throw new Error(result.error || 'Erro desconhecido');
        }

    } catch (error) {
        console.error('Erro na atualização do cache:', error);
        statusDiv.innerHTML = `<span style="color: red;">❌ Erro na atualização: ${error.message}</span>`;
        addConsoleMessage('error', `Erro na atualização do cache: ${error.message}`);
    }
}

// BUSCA DE LAYOUTS DO BANCO DE DADOS
let availableLayouts = [];
let selectedLayoutFromDatabase = null;
let availableLayoutsForUpload = [];
let selectedLayoutFromDatabaseForUpload = null;

async function searchLayoutsFromDatabase() {
    const statusDiv = document.getElementById('generationStatus');
    const searchResults = document.getElementById('layoutSearchResults');
    const layoutSelect = document.getElementById('layoutSelect');
    const loadBtn = document.querySelector('.load-btn');

    try {
        statusDiv.innerHTML = '🔍 Buscando layouts no banco de dados...';
        addConsoleMessage('info', 'Buscando layouts MQSeries NFe no banco de dados...');

        const response = await fetch('http://localhost:5214/api/layoutdatabase/mqseries-nfe', {
            method: 'GET',
            headers: {
                'Content-Type': 'application/json'
            }
        });

        if (!response.ok) {
            throw new Error(`Erro ao buscar layouts: ${response.statusText}`);
        }

        const result = await response.json();

        if (result.success && result.layouts && result.layouts.length > 0) {
            availableLayouts = result.layouts;
            
            // Limpar opções anteriores
            layoutSelect.innerHTML = '<option value="">Selecione um layout...</option>';
            
            // Adicionar layouts encontrados
            result.layouts.forEach((layout, index) => {
                const option = document.createElement('option');
                option.value = index;
                option.textContent = `${layout.name} (ID: ${layout.id}) - ${layout.description || 'Sem descrição'}`;
                layoutSelect.appendChild(option);
            });

            // Mostrar resultados
            searchResults.style.display = 'block';
            statusDiv.innerHTML = `<span style="color: green;">✅ Encontrados ${result.totalFound} layouts!</span>`;
            addConsoleMessage('success', `Encontrados ${result.totalFound} layouts MQSeries NFe`);

            // Habilitar seleção
            layoutSelect.addEventListener('change', function() {
                loadBtn.disabled = this.value === '';
            });

        } else {
            statusDiv.innerHTML = '<span style="color: orange;">⚠️ Nenhum layout encontrado</span>';
            addConsoleMessage('warning', 'Nenhum layout MQSeries NFe encontrado no banco');
            searchResults.style.display = 'none';
        }

    } catch (error) {
        console.error('Erro na busca de layouts:', error);
        statusDiv.innerHTML = `<span style="color: red;">❌ Erro na busca: ${error.message}</span>`;
        addConsoleMessage('error', `Erro na busca de layouts: ${error.message}`);
        searchResults.style.display = 'none';
    }
}

function loadSelectedLayout() {
    const layoutSelect = document.getElementById('layoutSelect');
    const statusDiv = document.getElementById('generationStatus');

    if (layoutSelect.value === '' || !availableLayouts[layoutSelect.value]) {
        statusDiv.innerHTML = '<span style="color: red;">❌ Selecione um layout válido</span>';
        return;
    }

    try {
        const selectedLayout = availableLayouts[layoutSelect.value];
        
        // Armazenar o layout selecionado globalmente
        selectedLayoutFromDatabase = selectedLayout;
        
        // Criar um arquivo virtual com o conteúdo descriptografado
        const layoutContent = selectedLayout.decryptedContent || selectedLayout.valueContent;
        
        if (!layoutContent) {
            throw new Error('Conteúdo do layout não disponível');
        }

        statusDiv.innerHTML = `<span style="color: green;">✅ Layout "${selectedLayout.name}" selecionado com sucesso!</span>`;
        addConsoleMessage('success', `Layout selecionado: ${selectedLayout.name} (ID: ${selectedLayout.id})`);

        // Mostrar informações do layout
        addConsoleMessage('info', `Tipo: ${selectedLayout.layoutType || 'N/A'}`);
        addConsoleMessage('info', `Última atualização: ${new Date(selectedLayout.lastUpdateDate).toLocaleString()}`);
        addConsoleMessage('info', 'Agora você pode gerar dados sintéticos usando este layout');

    } catch (error) {
        console.error('Erro ao carregar layout:', error);
        statusDiv.innerHTML = `<span style="color: red;">❌ Erro ao carregar layout: ${error.message}</span>`;
        addConsoleMessage('error', `Erro ao carregar layout: ${error.message}`);
    }
}

// FUNÇÕES ESPECÍFICAS PARA A ABA DE UPLOAD
async function searchLayoutsFromDatabaseForUpload() {
    const statusDiv = document.getElementById('uploadStatus');
    const searchResults = document.getElementById('layoutSearchResultsUpload');
    const layoutSelect = document.getElementById('layoutSelectUpload');
    const loadBtn = document.querySelector('#layoutSearchResultsUpload .load-btn');

    try {
        statusDiv.innerHTML = '🔍 Buscando layouts no banco de dados...';
        addConsoleMessage('info', 'Buscando layouts MQSeries NFe no banco de dados para processamento...');

        const response = await fetch('http://localhost:5214/api/layoutdatabase/mqseries-nfe', {
            method: 'GET',
            headers: {
                'Content-Type': 'application/json'
            }
        });

        if (!response.ok) {
            throw new Error(`Erro ao buscar layouts: ${response.statusText}`);
        }

        const result = await response.json();

        if (result.success && result.layouts && result.layouts.length > 0) {
            availableLayoutsForUpload = result.layouts;
            
            // Limpar opções anteriores
            layoutSelect.innerHTML = '<option value="">Selecione um layout...</option>';
            
            // Adicionar layouts encontrados
            result.layouts.forEach((layout, index) => {
                const option = document.createElement('option');
                option.value = index;
                option.textContent = `${layout.name} (ID: ${layout.id}) - ${layout.description || 'Sem descrição'}`;
                layoutSelect.appendChild(option);
            });

            // Mostrar resultados
            searchResults.style.display = 'block';
            statusDiv.innerHTML = `<span style="color: green;">✅ Encontrados ${result.totalFound} layouts!</span>`;
            addConsoleMessage('success', `Encontrados ${result.totalFound} layouts MQSeries NFe para processamento`);

            // Habilitar seleção
            layoutSelect.addEventListener('change', function() {
                loadBtn.disabled = this.value === '';
            });

        } else {
            statusDiv.innerHTML = '<span style="color: orange;">⚠️ Nenhum layout encontrado</span>';
            addConsoleMessage('warning', 'Nenhum layout MQSeries NFe encontrado no banco para processamento');
            searchResults.style.display = 'none';
        }

    } catch (error) {
        console.error('Erro na busca de layouts:', error);
        statusDiv.innerHTML = `<span style="color: red;">❌ Erro na busca: ${error.message}</span>`;
        addConsoleMessage('error', `Erro na busca de layouts: ${error.message}`);
        searchResults.style.display = 'none';
    }
}

function loadSelectedLayoutForUpload() {
    const layoutSelect = document.getElementById('layoutSelectUpload');
    const statusDiv = document.getElementById('uploadStatus');

    if (layoutSelect.value === '' || !availableLayoutsForUpload[layoutSelect.value]) {
        statusDiv.innerHTML = '<span style="color: red;">❌ Selecione um layout válido</span>';
        return;
    }

    try {
        const selectedLayout = availableLayoutsForUpload[layoutSelect.value];
        
        // Armazenar o layout selecionado globalmente para upload
        selectedLayoutFromDatabaseForUpload = selectedLayout;
        
        // Criar um arquivo virtual com o conteúdo descriptografado
        const layoutContent = selectedLayout.decryptedContent || selectedLayout.valueContent;
        
        if (!layoutContent) {
            throw new Error('Conteúdo do layout não disponível');
        }

        statusDiv.innerHTML = `<span style="color: green;">✅ Layout "${selectedLayout.name}" selecionado para processamento!</span>`;
        addConsoleMessage('success', `Layout selecionado para processamento: ${selectedLayout.name} (ID: ${selectedLayout.id})`);

        // Mostrar informações do layout
        addConsoleMessage('info', `Tipo: ${selectedLayout.layoutType || 'N/A'}`);
        addConsoleMessage('info', `Última atualização: ${new Date(selectedLayout.lastUpdateDate).toLocaleString()}`);
        addConsoleMessage('info', 'Agora você pode fazer upload do documento TXT para processamento');

    } catch (error) {
        console.error('Erro ao carregar layout:', error);
        statusDiv.innerHTML = `<span style="color: red;">❌ Erro ao carregar layout: ${error.message}</span>`;
        addConsoleMessage('error', `Erro ao carregar layout: ${error.message}`);
    }
}

// GERADOR DE DADOS - NOVA VERSÃO COM MÚLTIPLOS ARQUIVOS
async function generateAndDownloadFiles() {
    // Verificar se há um layout selecionado do banco
    if (!selectedLayoutFromDatabase) {
        const statusDiv = document.getElementById('generationStatus');
        statusDiv.innerHTML = '<span style="color: red;">❌ Selecione um layout do banco de dados primeiro</span>';
        addConsoleMessage('error', 'Erro: Nenhum layout selecionado do banco');
        return;
    }

    const numberOfFiles = parseInt(document.getElementById('numberOfFiles').value) || 5;
    const statusDiv = document.getElementById('generationStatus');

    if (numberOfFiles < 1 || numberOfFiles > 100) {
        statusDiv.innerHTML = '<span style="color: red;">❌ O número de arquivos deve estar entre 1 e 100</span>';
        addConsoleMessage('error', 'Erro: Número de arquivos inválido');
        return;
    }

    statusDiv.innerHTML = `⏳ Gerando ZIP com ${numberOfFiles} arquivo(s)...`;
    addConsoleMessage('info', `Iniciando geração de ZIP com ${numberOfFiles} arquivo(s)...`);

    try {
        const formData = new FormData();
        
        // Criar um arquivo virtual com o layout selecionado do banco
        const layoutContent = selectedLayoutFromDatabase.decryptedContent || selectedLayoutFromDatabase.valueContent;
        const blob = new Blob([layoutContent], { type: 'application/xml' });
        const file = new File([blob], `${selectedLayoutFromDatabase.name}.xml`, { type: 'application/xml' });
        
        formData.append("layoutFile", file);
        formData.append("numberOfRecords", 2); // Fixo em 2 registros por arquivo
        formData.append("numberOfFiles", numberOfFiles);
        formData.append("useAI", "true"); // Sempre usar IA

        const response = await fetch('http://localhost:5214/api/datageneration/generate-synthetic-zip', {
            method: 'POST',
            body: formData
        });

        if (!response.ok) {
            throw new Error(`Erro ao gerar ZIP: ${response.statusText}`);
        }

        // Verificar se é um arquivo ZIP
        const contentType = response.headers.get('content-type');
        if (contentType && contentType.includes('application/zip')) {
            const blob = await response.blob();
            const url = window.URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `dados_sinteticos_${new Date().toISOString().slice(0, 19).replace(/:/g, '-')}.zip`;
            document.body.appendChild(a);
            a.click();
            window.URL.revokeObjectURL(url);
            document.body.removeChild(a);
            
            statusDiv.innerHTML = '<span style="color: green;">✅ ZIP gerado e baixado com sucesso!</span>';
            addConsoleMessage('success', `🎉 ZIP gerado com sucesso! Contém ${numberOfFiles} arquivo(s) com ${recordsPerFile} registros cada.`);
        } else {
            throw new Error('Resposta não é um arquivo ZIP válido');
        }
    } catch (error) {
        console.error('Erro na geração:', error);
        statusDiv.innerHTML = `<span style="color: red;">❌ Erro na geração: ${error.message}</span>`;
        addConsoleMessage('error', `Erro na geração: ${error.message}`);
    }
}

// Manter função antiga para compatibilidade (se necessário)
async function generateData() {
    // Usar o campo de layout XML da aba de geração
    const layoutFile = document.getElementById('genLayoutFile').files[0];
    const sampleFile = document.getElementById('sampleDataFile')?.files[0];
    const recordCount = document.getElementById('recordCount')?.value || 10;
    const statusDiv = document.getElementById('generationStatus');

    if (!layoutFile) {
        statusDiv.innerHTML = '<span style="color: red;">Selecione o layout XML</span>';
        addConsoleMessage('error', 'Erro: Selecione o layout XML para gerar dados');
        return;
    }

    statusDiv.innerHTML = 'Gerando dados...';
    addConsoleMessage('info', 'Iniciando geração de dados sintéticos...');

    const formData = new FormData();
    formData.append("layoutFile", layoutFile);
    formData.append("records", recordCount);

    if (sampleFile) {
        formData.append("sampleFile", sampleFile);
        addConsoleMessage('info', 'Arquivo de amostra incluído');
    }

    try {
        const response = await fetch('http://localhost:5214/api/datagenerator/generate-from-layout', {
            method: 'POST',
            body: formData
        });

        const result = await response.json();

        if (result.success) {
            statusDiv.innerHTML = `<span style="color: green;">✅ Gerados ${result.recordsGenerated} registros em ${result.generationTime}</span>`;
            addConsoleMessage('success', `Dados sintéticos gerados: ${result.recordsGenerated} registros`);
        } else {
            statusDiv.innerHTML = `<span style="color: red;">Erro: ${result.error}</span>`;
            addConsoleMessage('error', `Erro na geração: ${result.error}`);
        }
    } catch (error) {
        statusDiv.innerHTML = `<span style="color: red;">Erro: ${error.message}</span>`;
        addConsoleMessage('error', `Erro na geração: ${error.message}`);
    }
}

function downloadGeneratedData() {
    const data = document.getElementById('generatedData')?.value;
    if (!data) {
        addConsoleMessage('error', 'Nenhum dado para baixar');
        return;
    }
    
    const blob = new Blob([data], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);

    const a = document.createElement('a');
    a.href = url;
    a.download = 'dados_sinteticos.txt';
    a.click();

    URL.revokeObjectURL(url);
    addConsoleMessage('success', 'Dados sintéticos exportados para arquivo');
}

// FUNÇÃO PARA TOGGLE DE INDENTAÇÃO
window.toggleIndentation = function() {
    indentationEnabled = !indentationEnabled;
    
    const button = document.querySelector('.toggle-indent-btn');
    if (button) {
        button.textContent = indentationEnabled ? '📐 Desativar Indentação' : '📐 Ativar Indentação';
        button.style.background = indentationEnabled ? '#ffc107' : '#9e9e9e';
    }
    
    // Re-renderizar o TXT com ou sem indentação
    // renderTxt(); // Desabilitado - usando fieldViewer
    
    addConsoleMessage('info', `Indentação ${indentationEnabled ? 'ativada' : 'desativada'}`);
};

// ============================================
// VISUALIZADOR DE CAMPOS INTEGRADO
// ============================================

let selectedFieldElement = null;
let fieldIdCounter = 1;

// Função principal para renderizar campos de uma linha
function renderFieldsForLine(lineName, fields) {
    const headerElement = document.getElementById('selectedLineName');
    const displayElement = document.getElementById('fieldDisplay');
    
    if (!headerElement || !displayElement) {
        console.error('Elementos do visualizador não encontrados');
        return;
    }
    
    if (!fields || fields.length === 0) {
        displayElement.innerHTML = '<div style="color: orange; padding: 20px;">⚠️ Nenhum campo encontrado para esta linha</div>';
        addConsoleMessage('warning', `Nenhum campo em ${lineName}`);
        return;
    }
    
    // Pegar o lineSequence do primeiro campo (todos os campos da mesma linha têm o mesmo lineSequence)
    const lineSequence = fields[0].lineSequence || '';
    
    if (!lineSequence) {
        displayElement.innerHTML = '<div style="color: red; padding: 20px;">⚠️ Sequência da linha não encontrada no JSON</div>';
        addConsoleMessage('error', `${lineName} sem lineSequence no JSON`);
        return;
    }
    
    // Buscar a posição no TXT onde está essa sequência
    const sequenceIndex = txtContent.indexOf(lineSequence);
    
    if (sequenceIndex === -1) {
        displayElement.innerHTML = `<div style="color: red; padding: 20px;">⚠️ Sequência "${lineSequence}" não encontrada no arquivo TXT</div>`;
        addConsoleMessage('error', `Sequência ${lineSequence} (${lineName}) não encontrada no TXT`);
        return;
    }
    
    // Extrair a linha de 600 caracteres a partir da sequência
    const rawLine = txtContent.substring(sequenceIndex, sequenceIndex + 600);
    
    console.log(`✅ ${lineName}: sequência "${lineSequence}" encontrada na posição ${sequenceIndex}, extraindo 600 chars`);
    
    // Verificar se é linha filha
    const isLinha020Child = /LINHA0(2[1-9]|3[0-9]|4[0-9])/.test(lineName);
    const isLinha050Child = /LINHA05[1-3]/.test(lineName);
    const parentLine = isLinha020Child ? 'LINHA020' : (isLinha050Child ? 'LINHA050' : null);
    
    // Atualizar header
    if (parentLine) {
        headerElement.textContent = `📋 ${lineName} (filha de ${parentLine}) | Sequência: ${lineSequence} | 600 posições`;
    } else {
        headerElement.textContent = `📋 ${lineName} | Sequência: ${lineSequence} | 600 posições`;
    }
    
    // Limpar seleção anterior
    selectedFieldElement = null;
    fieldIdCounter = 1;
    
    // Criar o conteúdo com a linha bruta e campos destacados
    displayElement.innerHTML = '';
    
    // Criar SPAN do Record (nome da linha)
    const recordSpan = createRecordSpan(lineName);
    displayElement.appendChild(recordSpan);
    
    // Criar badge de sequência
    const sequenceBadge = createSequenceBadge(lineName, lineSequence);
    displayElement.appendChild(sequenceBadge);
    
    // Ordenar campos por sequence
    const sortedFields = fields.sort((a, b) => a.sequence - b.sequence);
    
    // Criar SPANs de cada campo sobre a linha bruta
    sortedFields.forEach(field => {
        const fieldSpan = createFieldSpan(field);
        displayElement.appendChild(fieldSpan);
    });
    
    addConsoleMessage('info', `Visualizando ${lineName}: sequência ${lineSequence}, ${fields.length} campos`);
}

function createRecordSpan(lineName) {
    const span = document.createElement('SPAN');
    const id = 'record_' + fieldIdCounter++;
    
    span.id = id;
    span.className = 'Record';
    span.title = lineName;
    span.textContent = lineName;
    
    span.onclick = function() {
        fieldElementClick(id, 'Record', 'RecordSelected', lineName);
    };
    
    span.onmouseover = function() {
        if (!span.className.includes('Selected')) {
            span.className = 'RecordOver Record';
        }
    };
    
    span.onmouseout = function() {
        if (!span.className.includes('Selected')) {
            span.className = 'Record';
        }
    };
    
    return span;
}

function createSequenceBadge(lineName, lineSequence) {
    const sequence = lineSequence || '---';
    
    const badge = document.createElement('SPAN');
    badge.className = 'Sequence';
    badge.title = `Sequencial: ${sequence} | Linha: ${lineName}`;
    badge.textContent = sequence;
    
    return badge;
}

function createFieldSpan(field) {
    const span = document.createElement('SPAN');
    const id = 'field_' + fieldIdCounter++;
    
    span.id = id;
    span.className = 'Field';
    span.title = `${field.fieldName} | Pos: ${field.start}-${field.start + field.length - 1} | Valor: "${field.value}"`;
    
    // Exibir valor com espaços preservados
    let displayValue = field.value || '';
    if (displayValue.trim() === '' && field.length > 0) {
        displayValue = '\u00A0'.repeat(field.length);
    } else {
        displayValue = displayValue.replace(/ /g, '\u00A0');
    }
    
    span.textContent = displayValue || '(vazio)';
    
    span.onclick = function() {
        fieldElementClick(id, 'Field', 'FieldSelected', field);
    };
    
    span.onmouseover = function() {
        if (!span.className.includes('Selected')) {
            span.className = 'FieldOver Field';
        }
    };
    
    span.onmouseout = function() {
        if (!span.className.includes('Selected')) {
            span.className = 'Field';
        }
    };
    
    return span;
}

function fieldElementClick(id, defaultClass, selectedClass, data) {
    // Remover seleção anterior
    if (selectedFieldElement) {
        const prevElement = document.getElementById(selectedFieldElement.id);
        if (prevElement) {
            prevElement.className = selectedFieldElement.defaultClass;
        }
    }
    
    // Adicionar nova seleção
    const element = document.getElementById(id);
    if (element) {
        element.className = selectedClass;
        selectedFieldElement = { id, defaultClass };
    }
    
    // Log no console
    if (typeof data === 'object' && data.fieldName) {
        addConsoleMessage('info', `Campo selecionado: ${data.fieldName} = "${data.value}"`);
    } else {
        addConsoleMessage('info', `Linha selecionada: ${data}`);
    }
}

function clearFieldSelection() {
    if (selectedFieldElement) {
        const prevElement = document.getElementById(selectedFieldElement.id);
        if (prevElement) {
            prevElement.className = selectedFieldElement.defaultClass;
        }
        selectedFieldElement = null;
    }
    addConsoleMessage('info', 'Seleção limpa');
}

// Função para destacar um campo específico no visualizador e fazer scroll até ele
function highlightFieldInViewer(field) {
    const displayElement = document.getElementById('fieldDisplay');
    if (!displayElement) {
        console.error('fieldDisplay não encontrado');
        return;
    }

    // Limpar destaque anterior
    const previousHighlighted = displayElement.querySelector('.Field.field-highlighted');
    if (previousHighlighted) {
        previousHighlighted.classList.remove('field-highlighted');
    }

    // Procurar o campo pelo title que contém o fieldName
    const fieldSpans = displayElement.querySelectorAll('.Field');
    let targetSpan = null;

    for (const span of fieldSpans) {
        const title = span.getAttribute('title') || '';
        // O title tem formato: "FieldName | Pos: X-Y | Valor: "value""
        if (title.includes(field.fieldName) && title.includes(`Pos: ${field.start}-${field.start + field.length - 1}`)) {
            targetSpan = span;
            break;
        }
    }

    if (targetSpan) {
        // Adicionar classe de destaque
        targetSpan.classList.add('field-highlighted');

        // Fazer scroll até o campo
        targetSpan.scrollIntoView({
            behavior: 'smooth',
            block: 'center',
            inline: 'center'
        });

        addConsoleMessage('success', `Campo "${field.fieldName}" destacado no visualizador`);
    } else {
        console.warn(`Campo ${field.fieldName} não encontrado no visualizador`);
        addConsoleMessage('warning', `Campo "${field.fieldName}" não encontrado no visualizador`);
    }
}

// FUNÇÃO PARA REDIMENSIONAR DINAMICAMENTE
function adjustLayoutHeights() {
    const structurePane = document.querySelector('#structureTabInternal .pane:first-child');
    const contentPane = document.querySelector('#structureTabInternal .pane:last-child');

    if (structurePane && contentPane) {
        const availableHeight = window.innerHeight * 0.7; // 70% da altura da janela
        structurePane.style.maxHeight = availableHeight + 'px';
        contentPane.style.maxHeight = availableHeight + 'px';
    }
}

// AJUSTAR LAYOUT AO REDIMENSIONAR A JANELA
window.addEventListener('resize', adjustLayoutHeights);

document.addEventListener('DOMContentLoaded', function () {
    setTimeout(adjustLayoutHeights, 100);
});

// FUNÇÃO PARA ABAS INTERNAS (APENAS 2)


// FUNÇÃO PARA ALTERNAR WRAP


// MOVER O BOTÃO "MOSTRAR PROBLEMAS IA" PARA DENTRO DO SUMMARY
function renderSummary(summary) {
    const summaryDiv = document.getElementById("summary");
    summaryDiv.innerHTML = `
    <div style="font-size: 12px; background: #f0f0f0; padding: 12px; border-radius: 6px; margin-bottom: 15px;">
      <div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 8px;">
        <strong>Resumo do Documento:</strong>
        <button onclick="highlightProblems()" style="padding: 6px 12px; font-size: 11px; background: #ffc107; color: black; border: none; border-radius: 4px; cursor: pointer;">
          ⭐ Análise IA
        </button>
      </div>
      <div>
        📄 Tipo: ${summary.documentType} | Versão: ${summary.layoutVersion}<br>
        📊 Linhas: ${summary.presentLines}/${summary.expectedLines} | 
        Campos: ${summary.totalFields}<br>
        <span style="color: green;">✓ Válidos: ${summary.validFields} (${summary.complianceRate.toFixed(1)}%)</span> | 
        <span style="color: orange;">⚠ Alertas: ${summary.warningFields}</span> | 
        <span style="color: red;">✗ Erros: ${summary.errorFields}</span>
        ${summary.missingLines > 0 ?
            `<br><span style="color: red;">🔴 Linhas ausentes: ${summary.missingLines}</span>` : ''
        }
      </div>
    </div>
  `;

    updateProcessingStatsFromSummary(summary);
}

// GARANTIR QUE A ANÁLISE IA APAREÇA NO SUMMARY
function displayAnalysisResults(analysis) {
    const summaryDiv = document.getElementById('analysisSummary');

    if (analysis && analysis.summary) {
        const scoreClass = analysis.summary.complianceScore >= 80 ? 'score-high' :
            analysis.summary.complianceScore >= 60 ? 'score-medium' : 'score-low';

        summaryDiv.style.display = 'block';
        summaryDiv.innerHTML = `
      <div class="compliance-score ${scoreClass}">
        📊 Compliance Score: ${analysis.summary.complianceScore}%
      </div>
      <div style="display: grid; grid-template-columns: repeat(4, 1fr); gap: 8px; font-size: 11px; margin-top: 10px;">
        <div class="analysis-item analysis-success">
          <strong>✅ Válidos:</strong> ${analysis.summary.success}
        </div>
        <div class="analysis-item analysis-warning">
          <strong>⚠ Alertas:</strong> ${analysis.summary.warnings}
        </div>
        <div class="analysis-item analysis-error">
          <strong>❌ Erros:</strong> ${analysis.summary.errors}
        </div>
        <div class="analysis-item analysis-error">
          <strong>🔴 Faltantes:</strong> ${analysis.summary.missingRequired}
        </div>
      </div>
    `;
    }
}

function calculateGlobalPosition(field) {
    console.group('🧮 Calculando Posição Global CORRETA');

    // Mapa de linhas para índices físicos (base 1 para este cálculo)
    // Linhas 021-049 compartilham o índice físico da LINHA020
    // Linhas 051-053 compartilham o índice físico da LINHA050
    const lineOrder = {
        'HEADER': 1,
        'LINHA000': 2, 'LINHA001': 3, 'LINHA002': 4, 'LINHA003': 5, 'LINHA004': 6,
        'LINHA005': 7, 'LINHA006': 8, 'LINHA007': 9, 'LINHA008': 10, 'LINHA009': 11,
        'LINHA010': 12, 'LINHA011': 13, 'LINHA012': 14, 'LINHA013': 15, 'LINHA014': 16,
        'LINHA015': 17, 'LINHA016': 18, 'LINHA017': 19, 'LINHA018': 20, 'LINHA019': 21,
        'LINHA020': 22,
        // LINHA021 a LINHA049 são filhas da LINHA020
        'LINHA021': 22, 'LINHA022': 22, 'LINHA023': 22, 'LINHA024': 22, 'LINHA025': 22,
        'LINHA026': 22, 'LINHA027': 22, 'LINHA028': 22, 'LINHA029': 22, 'LINHA030': 22,
        'LINHA031': 22, 'LINHA032': 22, 'LINHA033': 22, 'LINHA034': 22, 'LINHA035': 22,
        'LINHA036': 22, 'LINHA037': 22, 'LINHA038': 22, 'LINHA039': 22, 'LINHA040': 22,
        'LINHA041': 22, 'LINHA042': 22, 'LINHA043': 22, 'LINHA044': 22, 'LINHA045': 22,
        'LINHA046': 22, 'LINHA047': 22, 'LINHA048': 22, 'LINHA049': 22,
        'LINHA050': 23,
        // LINHA051 a LINHA053 são filhas da LINHA050
        'LINHA051': 23, 'LINHA052': 23, 'LINHA053': 23,
        // LINHA054 em diante são linhas físicas independentes
        'LINHA054': 24, 'LINHA055': 25, 'LINHA056': 26, 'LINHA057': 27, 'LINHA058': 28,
        'LINHA059': 29, 'LINHA060': 30, 'LINHA061': 31, 'LINHA062': 32, 'LINHA063': 33,
        'LINHA064': 34, 'LINHA065': 35, 'LINHA066': 36, 'LINHA067': 37, 'LINHA068': 38,
        'LINHA069': 39, 'LINHA070': 40, 'LINHA071': 41, 'LINHA072': 42,
        'LINHA080': 43, 'LINHA081': 44, 'LINHA082': 45, 'LINHA083': 46, 'LINHA084': 47,
        'LINHA085': 48, 'LINHA086': 49, 'LINHA087': 50, 'LINHA088': 51, 'LINHA089': 52,
        'LINHA090': 53, 'LINHA091': 54, 'LINHA092': 55, 'LINHA093': 56, 'LINHA094': 57,
        'LINHA095': 58, 'LINHA096': 59, 'LINHA097': 60, 'LINHA098': 61
    };

    const lineNumber = lineOrder[field.lineName];
    const lineLength = 600;

    if (!lineNumber) {
        console.error('❌ Linha não mapeada:', field.lineName);
        console.groupEnd();
        return field.start - 1; // Fallback
    }

    // ✅ CORREÇÃO: Posição global = (linhas anteriores * 600) + posição na linha atual
    const globalStart = ((lineNumber - 1) * lineLength) + (field.start - 1);

    console.log('📊 Dados do campo:', {
        campo: field.fieldName,
        linha: field.lineName,
        numeroLinha: lineNumber,
        startNaLinha: field.start,
        length: field.length,
        globalStart: globalStart
    });

    console.log('🧮 Cálculo:', {
        linhasAnteriores: lineNumber - 1,
        offsetLinhasAnteriores: (lineNumber - 1) * lineLength,
        posicaoNaLinha: field.start - 1,
        totalGlobal: globalStart
    });

    console.log('📍 Resultado:', {
        linhaEsperada: lineNumber,
        linhaCalculada: Math.floor(globalStart / lineLength) + 1,
        correto: (Math.floor(globalStart / lineLength) + 1) === lineNumber
    });

    console.groupEnd();
    return globalStart;
}

function testCampoLINHA000() {
    console.log('🧪 TESTE: Campo da LINHA000');

    // Simular exatamente o campo que você clicou
    const testField = {
        fieldName: 'ControleDaVersaoDoArquivo',
        lineName: 'LINHA000',
        start: 10, // Posição RELATIVA na LINHA000
        length: 3
    };

    const globalStart = calculateGlobalPosition(testField);
    console.log('🎯 Resultado do teste:', {
        campo: testField.fieldName,
        linha: testField.lineName,
        startRelativo: testField.start,
        globalStart: globalStart,
        linhaCalculada: Math.floor(globalStart / 600) + 1,
        linhaEsperada: 2
    });

    // Deve mostrar: linhaCalculada: 2 (correto!)
}

// ===========================
// VISUALIZADOR INTEGRADO
// ===========================

let viewerSelectedElement = null;
let viewerElementIdCounter = 1;

// Renderizar visualizador integrado após processamento
window.renderIntegratedViewer = function() {
    if (!parsedResult || !parsedResult.fields || !parsedResult.text) {
        console.log('Dados insuficientes para renderizar visualizador');
        return;
    }

    // Esconder placeholder e mostrar visualizador
    document.getElementById('viewerPlaceholder').style.display = 'none';
    document.getElementById('integratedViewer').style.display = 'flex';

    // Atualizar info do documento
    const docInfo = document.getElementById('viewerDocInfo');
    const summary = parsedResult.summary || {};
    docInfo.textContent = `${summary.documentType || 'Documento'} | Versão: ${summary.layoutVersion || 'N/A'} | ${summary.totalFields || 0} campos`;

    // Renderizar documento
    renderViewerDocument();
    
    addConsoleMessage('success', '✅ Visualizador integrado renderizado com sucesso');
};

// Renderizar o documento no visualizador
function renderViewerDocument() {
    const tbody = document.getElementById('viewerDocumentBody');
    tbody.innerHTML = '';

    if (!parsedResult || !parsedResult.fields) {
        tbody.innerHTML = '<tr><td style="padding: 40px; text-align: center; color: #999;">Nenhum campo encontrado</td></tr>';
        return;
    }

    // Agrupar campos por linha
    const fieldsByLine = {};
    parsedResult.fields.forEach(field => {
        const lineName = field.lineName || 'OUTROS';
        if (!fieldsByLine[lineName]) {
            fieldsByLine[lineName] = [];
        }
        fieldsByLine[lineName].push(field);
    });

    // Ordenar linhas
    const lineNames = Object.keys(fieldsByLine).sort();

    // Renderizar cada linha
    lineNames.forEach(lineName => {
        const lineFields = fieldsByLine[lineName];
        const tr = document.createElement('tr');
        tr.className = 'viewer-line-row';

        const td = document.createElement('td');

        // Nome da linha (Record)
        const recordSpan = document.createElement('span');
        recordSpan.className = 'Record';
        recordSpan.textContent = lineName;
        recordSpan.onclick = () => showViewerLineProperties(lineName, lineFields);
        td.appendChild(recordSpan);

        // Sequência
        const seqSpan = document.createElement('span');
        seqSpan.className = 'Sequence';
        seqSpan.textContent = lineFields[0]?.lineSequence || '000';
        seqSpan.title = `Sequência: ${lineFields[0]?.lineSequence || '000'}`;
        td.appendChild(seqSpan);

        // Campos
        lineFields.forEach(field => {
            const fieldSpan = document.createElement('span');
            fieldSpan.className = 'Field';
            fieldSpan.textContent = field.value || ' ';
            fieldSpan.id = `viewer-field-${viewerElementIdCounter++}`;
            fieldSpan.setAttribute('data-field-name', field.fieldName);
            fieldSpan.setAttribute('data-line-name', field.lineName);
            fieldSpan.setAttribute('data-start', field.start);
            fieldSpan.setAttribute('data-length', field.length);
            fieldSpan.onclick = () => showViewerFieldProperties(field, fieldSpan);
            td.appendChild(fieldSpan);
        });

        tr.appendChild(td);
        tbody.appendChild(tr);
    });
}

// Mostrar propriedades do campo
function showViewerFieldProperties(field, element) {
    // Desselecionar elemento anterior
    if (viewerSelectedElement) {
        viewerSelectedElement.classList.remove('selected');
    }

    // Selecionar novo elemento
    viewerSelectedElement = element;
    element.classList.add('selected');

    // Atualizar painel de propriedades
    const content = document.getElementById('viewerPropertiesContent');
    content.innerHTML = `
        <h5>📋 Informações do Campo</h5>
        <p><span class="prop-label">Campo:</span> <span class="prop-value">${field.fieldName || 'N/A'}</span></p>
        <p><span class="prop-label">Linha:</span> <span class="prop-value">${field.lineName || 'N/A'}</span></p>
        <p><span class="prop-label">Valor:</span> <span class="prop-value">${field.value || '(vazio)'}</span></p>
        <p><span class="prop-label">Tipo de Dado:</span> <span class="prop-value">${field.dataType || 'N/A'}</span></p>
        
        <h5>📏 Posicionamento</h5>
        <p><span class="prop-label">Posição Inicial:</span> <span class="prop-value">${field.start || 0}</span></p>
        <p><span class="prop-label">Comprimento:</span> <span class="prop-value">${field.length || 0}</span></p>
        <p><span class="prop-label">Alinhamento:</span> <span class="prop-value">${field.alignment || 'N/A'}</span></p>
        
        <h5>✅ Validação</h5>
        <p><span class="prop-label">Status:</span> <span class="prop-value">${field.status || 'ok'}</span></p>
        <p><span class="prop-label">Obrigatório:</span> <span class="prop-value">${field.isRequired ? 'Sim' : 'Não'}</span></p>
        ${field.validationMessage ? `<p><span class="prop-label">Mensagem:</span> <span class="prop-value">${field.validationMessage}</span></p>` : ''}
        
        <h5>📊 Metadados</h5>
        <p><span class="prop-label">Sequência:</span> <span class="prop-value">${field.sequence || 'N/A'}</span></p>
        <p><span class="prop-label">Ocorrência:</span> <span class="prop-value">${field.occurrence || 1}</span></p>
    `;

    // Abrir painel se não estiver aberto
    const panel = document.getElementById('viewerPropertiesPanel');
    if (!panel.classList.contains('open')) {
        toggleViewerProperties();
    }
}

// Mostrar propriedades da linha
function showViewerLineProperties(lineName, fields) {
    const content = document.getElementById('viewerPropertiesContent');
    const totalFields = fields.length;
    const validFields = fields.filter(f => f.status === 'ok').length;
    const errorFields = fields.filter(f => f.status === 'error').length;
    const warningFields = fields.filter(f => f.status === 'warning').length;

    content.innerHTML = `
        <h5>📋 Informações da Linha</h5>
        <p><span class="prop-label">Nome:</span> <span class="prop-value">${lineName}</span></p>
        <p><span class="prop-label">Total de Campos:</span> <span class="prop-value">${totalFields}</span></p>
        <p><span class="prop-label">Sequência:</span> <span class="prop-value">${fields[0]?.lineSequence || '000'}</span></p>
        
        <h5>✅ Estatísticas de Validação</h5>
        <p><span class="prop-label">Válidos:</span> <span class="prop-value" style="color: green;">${validFields}</span></p>
        <p><span class="prop-label">Alertas:</span> <span class="prop-value" style="color: orange;">${warningFields}</span></p>
        <p><span class="prop-label">Erros:</span> <span class="prop-value" style="color: red;">${errorFields}</span></p>
        
        <h5>📊 Campos da Linha</h5>
        <div style="max-height: 300px; overflow-y: auto; background: #f8f9fa; padding: 10px; border-radius: 4px;">
            ${fields.map((f, i) => `
                <div style="padding: 5px; border-bottom: 1px solid #dee2e6;">
                    <strong>${i + 1}. ${f.fieldName}</strong><br>
                    <span style="font-size: 11px; color: #666;">
                        Pos: ${f.start} | Len: ${f.length} | Valor: "${f.value || '(vazio)'}"
                    </span>
                </div>
            `).join('')}
        </div>
    `;

    // Abrir painel se não estiver aberto
    const panel = document.getElementById('viewerPropertiesPanel');
    if (!panel.classList.contains('open')) {
        toggleViewerProperties();
    }
}

// Toggle painel de propriedades
window.toggleViewerProperties = function() {
    const panel = document.getElementById('viewerPropertiesPanel');
    const icon = document.getElementById('propertiesBtnIcon');
    
    if (panel.classList.contains('open')) {
        panel.classList.remove('open');
        icon.textContent = '👁️';
    } else {
        panel.classList.add('open');
        icon.textContent = '👁️‍🗨️';
    }
};

// Fechar painel de propriedades
window.closeViewerProperties = function() {
    const panel = document.getElementById('viewerPropertiesPanel');
    const icon = document.getElementById('propertiesBtnIcon');
    panel.classList.remove('open');
    icon.textContent = '👁️';
    
    // Desselecionar elemento
    if (viewerSelectedElement) {
        viewerSelectedElement.classList.remove('selected');
        viewerSelectedElement = null;
    }
};

// Exportar HTML do visualizador
window.exportViewerHTML = function() {
    const tbody = document.getElementById('viewerDocumentBody');
    if (!tbody || !tbody.innerHTML) {
        alert('Nenhum conteúdo para exportar');
        return;
    }

    const html = `<!DOCTYPE html>
<html lang="pt-BR">
<head>
    <meta charset="UTF-8">
    <title>Documento Exportado - ${parsedResult?.summary?.documentType || 'Layout'}</title>
    <style>
        body { font-family: 'Courier New', monospace; font-size: 12px; padding: 20px; background: #f5f5f5; }
        table { width: 100%; border-collapse: separate; border-spacing: 1px; background: white; }
        td { padding: 2px; vertical-align: top; }
        .Record { padding: 4px 8px; font-weight: bold; background: #e9ecef; border-radius: 3px; display: inline-block; }
        .Sequence { background: #007bff; color: white; padding: 2px 8px; margin: 0 5px; border-radius: 4px; font-size: 10px; font-weight: bold; display: inline-block; }
        .Field { padding: 0; white-space: pre; display: inline; }
        .viewer-line-row { border-bottom: 1px solid #e0e0e0; }
    </style>
</head>
<body>
    <h2>📄 ${parsedResult?.summary?.documentType || 'Documento'} - Versão ${parsedResult?.summary?.layoutVersion || 'N/A'}</h2>
    <table>
        <tbody>
            ${tbody.innerHTML}
        </tbody>
    </table>
</body>
</html>`;

    const blob = new Blob([html], { type: 'text/html' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `documento-${Date.now()}.html`;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(url);

    addConsoleMessage('success', '💾 HTML exportado com sucesso');
};