class IAStarSystem {
    constructor() {
        this.issues = [];
        this.starElements = new Map();
    }

    // Analisa o layout e identifica problemas para as estrelas
    analyzeForStars(layoutData, txtContent) {
        this.issues = [];

        const lines = txtContent.split('\n');

        layoutData.layout.elements.forEach(line => {
            const lineIdentifier = line.initialValue;
            const lineContent = lines.find(l => l.startsWith(lineIdentifier)) || '';

            line.elements.forEach(field => {
                if (field.type === "FieldElementVO") {
                    const fieldValue = this.extractFieldValue(lineContent, field);
                    const issues = this.detectFieldIssues(field, fieldValue);

                    if (issues.length > 0) {
                        this.issues.push({
                            line: line.name,
                            field: field.name,
                            fieldGuid: field.elementGuid,
                            sequence: field.sequence,
                            issues: issues,
                            severity: this.getHighestSeverity(issues)
                        });
                    }
                }
            });
        });

        return this.issues;
    }

    // Detecta problemas específicos no campo
    detectFieldIssues(field, value) {
        const issues = [];

        // 1. Campo obrigatório vazio
        if (field.isRequired && (!value || value.trim() === '')) {
            issues.push({
                type: 'required_empty',
                severity: 'error',
                message: 'Campo obrigatório está vazio',
                suggestion: 'Preencha este campo com dados válidos'
            });
        }

        // 2. Tamanho excedido
        if (value && value.length > field.lengthField) {
            issues.push({
                type: 'length_exceeded',
                severity: 'error',
                message: `Excede tamanho máximo (${field.lengthField} chars)`,
                suggestion: `Reduza para ${field.lengthField} caracteres`
            });
        }

        // 3. Preenchimento insuficiente
        if (value && value.length < field.lengthField && field.alignmentType === 'Left') {
            issues.push({
                type: 'underfilled',
                severity: 'warning',
                message: `Preenchimento insuficiente (${value.length}/${field.lengthField})`,
                suggestion: 'Complete com espaços ou zeros'
            });
        }

        // 4. Validações específicas
        if (field.name.includes('CNPJ') && !this.validateCNPJ(value)) {
            issues.push({
                type: 'invalid_cnpj',
                severity: 'error',
                message: 'CNPJ inválido',
                suggestion: 'Verifique o número do CNPJ'
            });
        }

        if (field.name.includes('CPF') && !this.validateCPF(value)) {
            issues.push({
                type: 'invalid_cpf',
                severity: 'error',
                message: 'CPF inválido',
                suggestion: 'Verifique o número do CPF'
            });
        }

        // 5. Campos numéricos com caracteres inválidos
        if ((field.name.includes('Valor') || field.name.includes('Total')) &&
            value && !/^[\d.,\s]+$/.test(value)) {
            issues.push({
                type: 'invalid_numeric',
                severity: 'warning',
                message: 'Possui caracteres não numéricos',
                suggestion: 'Use apenas números, ponto e vírgula'
            });
        }

        return issues;
    }

    // Adiciona estrelas IA aos elementos da tree
    addStarsToTree() {
        // Remove estrelas existentes
        this.removeAllStars();

        // Adiciona novas estrelas
        this.issues.forEach(issue => {
            const fieldElement = this.findFieldElement(issue.fieldGuid);
            if (fieldElement) {
                this.addStarToField(fieldElement, issue);
            }
        });
    }

    // Encontra o elemento do campo na árvore
    findFieldElement(fieldGuid) {
        const fieldElements = document.querySelectorAll('.tree-item');
        for (let element of fieldElements) {
            if (element.textContent.includes(fieldGuid) ||
                element.querySelector(`[data-guid="${fieldGuid}"]`)) {
                return element;
            }
        }
        return null;
    }

    // Adiciona estrela a um campo específico
    addStarToField(fieldElement, issue) {
        const star = document.createElement('div');
        star.className = `ai-star ${issue.severity}`;
        star.title = this.generateStarTooltip(issue);
        star.innerHTML = '⭐';

        star.addEventListener('click', (e) => {
            e.stopPropagation();
            this.showIssueDetails(issue);
        });

        fieldElement.style.position = 'relative';
        fieldElement.appendChild(star);

        this.starElements.set(issue.fieldGuid, star);
    }

    // Gera tooltip para a estrela
    generateStarTooltip(issue) {
        return `Problemas identificados pela IA:\n${issue.issues.map(i => `• ${i.message}`).join('\n')}\n\nClique para detalhes`;
    }

    // Remove todas as estrelas
    removeAllStars() {
        this.starElements.forEach((star, guid) => {
            star.remove();
        });
        this.starElements.clear();
    }

    // Mostra detalhes do problema
    showIssueDetails(issue) {
        const modal = this.createIssueModal(issue);
        document.body.appendChild(modal);
    }

    // Cria modal de detalhes do problema
    createIssueModal(issue) {
        const modal = document.createElement('div');
        modal.className = 'ai-issue-modal';
        modal.style.cssText = `
      position: fixed;
      top: 50%;
      left: 50%;
      transform: translate(-50%, -50%);
      background: white;
      padding: 20px;
      border-radius: 8px;
      box-shadow: 0 4px 20px rgba(0,0,0,0.3);
      z-index: 1000;
      max-width: 500px;
      width: 90%;
    `;

        modal.innerHTML = `
      <h3>🤖 Análise IA - ${issue.field}</h3>
      <div class="issue-severity ${issue.severity}">
        Severidade: ${issue.severity === 'error' ? '❌ Erro' : '⚠ Alerta'}
      </div>
      <div class="issue-list">
        ${issue.issues.map(iss => `
          <div class="issue-item">
            <strong>${iss.message}</strong>
            <p>${iss.suggestion}</p>
          </div>
        `).join('')}
      </div>
      <div class="modal-actions">
        <button onclick="this.closest('.ai-issue-modal').remove()">Fechar</button>
        <button onclick="highlightIssue('${issue.fieldGuid}')">Mostrar no Texto</button>
      </div>
    `;

        // Overlay de fundo
        const overlay = document.createElement('div');
        overlay.style.cssText = `
      position: fixed;
      top: 0;
      left: 0;
      width: 100%;
      height: 100%;
      background: rgba(0,0,0,0.5);
      z-index: 999;
    `;
        overlay.onclick = () => {
            modal.remove();
            overlay.remove();
        };

        document.body.appendChild(overlay);
        return modal;
    }

    // Utilitários de validação
    validateCNPJ(cnpj) {
        if (!cnpj || cnpj.trim() === '') return true;
        const clean = cnpj.replace(/\D/g, '');
        return clean.length === 14;
    }

    validateCPF(cpf) {
        if (!cpf || cpf.trim() === '') return true;
        const clean = cpf.replace(/\D/g, '');
        return clean.length === 11;
    }

    extractFieldValue(lineContent, field) {
        const start = field.startValue - 1;
        return lineContent.substring(start, start + field.lengthField);
    }

    getHighestSeverity(issues) {
        if (issues.some(issue => issue.severity === 'error')) return 'error';
        if (issues.some(issue => issue.severity === 'warning')) return 'warning';
        return 'info';
    }
}

// Funções globais para integração
window.iaStarSystem = new IAStarSystem();

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

window.highlightIssue = function (fieldGuid) {
    // Implementar highlight do campo no texto
    const issue = window.iaStarSystem.issues.find(i => i.fieldGuid === fieldGuid);
    if (issue) {
        addConsoleMessage('info', `Navegando para o campo: ${issue.field}`);
        // Aqui você implementaria a navegação para o campo
    }
};

// Atualiza estatísticas no console
function updateProcessingStats(issues) {
    const errorCount = issues.filter(i => i.severity === 'error').length;
    const warningCount = issues.filter(i => i.severity === 'warning').length;

    document.getElementById('statErrorFields').textContent = errorCount;
    document.getElementById('statWarningFields').textContent = warningCount;

    if (window.parsedResult) {
        const totalFields = window.parsedResult.layout.elements.reduce(
            (acc, line) => acc + line.elements.length, 0
        );
        const validFields = totalFields - errorCount - warningCount;
        const compliance = Math.round((validFields / totalFields) * 100);

        document.getElementById('statTotalFields').textContent = totalFields;
        document.getElementById('statValidFields').textContent = validFields;
        document.getElementById('statCompliance').textContent = compliance + '%';
    }
}

// layout-detector.js
class LayoutDetector {
    static detectLayoutType(txtContent) {
        const lines = txtContent.split('\n');

        // Verifica se é iDoc (linhas variáveis com prefixos específicos)
        if (this.isIDoc(lines)) {
            return 'idoc';
        }
        // Verifica se é MQSeries (linhas fixas de 600 caracteres)
        else if (this.isMQSeries(lines)) {
            return 'mqseries';
        }
        // Verifica se é XML
        else if (this.isXML(txtContent)) {
            return 'xml';
        }
        else {
            return 'unknown';
        }
    }

    static isIDoc(lines) {
        // iDoc tem prefixos como EDI_DC40, ZRSDM_NFE_400, etc.
        const idocPatterns = [
            /^EDI_DC40/,
            /^ZRSDM_NFE_/,
            /^ZRSDM_.*_\d{3}$/,
            /^\w+_\w+_\d{3}/
        ];

        return lines.some(line =>
            idocPatterns.some(pattern => pattern.test(line.trim()))
        );
    }

    static isMQSeries(lines) {
        // MQSeries tem linhas fixas de 600 caracteres
        if (lines.length === 0) return false;

        const hasFixedLength = lines.every(line =>
            line.length === 600 || line.trim().length === 0
        );

        const hasSequence = lines.some(line =>
            line.length >= 6 && /^\d{6}$/.test(line.substring(0, 6))
        );

        return hasFixedLength && hasSequence;
    }

    static isXML(content) {
        try {
            const parser = new DOMParser();
            const xmlDoc = parser.parseFromString(content, "text/xml");
            return xmlDoc.getElementsByTagName("parsererror").length === 0;
        } catch {
            return false;
        }
    }

    static getLayoutConfig(layoutType, content) {
        const baseConfig = {
            mqseries: {
                name: 'MQSeries',
                lineLength: 600,
                hasFixedLength: true,
                encoding: 'UTF-8',
                splitMethod: 'fixed',
                validationRules: LayoutDetector.mqseriesValidationRules()
            },
            idoc: {
                name: 'iDoc',
                lineLength: null, // Variável
                hasFixedLength: false,
                encoding: 'UTF-8',
                splitMethod: 'linebreak',
                validationRules: LayoutDetector.idocValidationRules(content)
            },
            xml: {
                name: 'XML',
                lineLength: null,
                hasFixedLength: false,
                encoding: 'UTF-8',
                splitMethod: 'xml',
                validationRules: LayoutDetector.xmlValidationRules()
            }
        };

        return baseConfig[layoutType] || baseConfig.mqseries;
    }

    static xmlValidationRules() {
        return {
            requiredElements: ['root'],
            encoding: 'UTF-8',
            validateStructure: true
        };
    }

    static mqseriesValidationRules() {
        return {
            requiredLines: ['HEADER', 'LINHA000', 'LINHA001', 'TRAILER'],
            lineOrder: ['HEADER', 'LINHA000', 'LINHA001', 'LINHA002', 'TRAILER'],
            maxLineLength: 600,
            minLineLength: 600
        };
    }

    static idocValidationRules(content) {
        const lines = content.split('\n');
        const lineLengths = lines.map(line => line.length);

        return {
            requiredSegments: ['EDI_DC40'],
            segmentPatterns: [
                /^EDI_DC40/,
                /^ZRSDM_NFE_\d+_IDE/,
                /^ZRSDM_NFE_\d+_EMIT/,
                /^ZRSDM_NFE_\d+_DET/,
                /^ZRSDM_NFE_\d+_PROD/
            ],
            maxLineLength: Math.max(...lineLengths),
            minLineLength: Math.min(...lineLengths.filter(len => len > 0)),
            avgLineLength: lineLengths.reduce((a, b) => a + b, 0) / lines.length
        };
    }

    static analyzeIDocStructure(content) {
        const lines = content.split('\n');
        const segments = {};

        lines.forEach((line, index) => {
            if (line.trim()) {
                // Extrai o segmento (ex: "ZRSDM_NFE_400_IDE000")
                const segmentMatch = line.match(/^(\w+_\w+_\d+_\w+)/);
                if (segmentMatch) {
                    const segment = segmentMatch[1];
                    segments[segment] = segments[segment] || [];
                    segments[segment].push({
                        lineNumber: index + 1,
                        content: line,
                        length: line.length
                    });
                }
            }
        });

        return segments;
    }
}