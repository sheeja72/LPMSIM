// Convert LPM_SIM_User_Guide.md to .docx using docx-js.
// Handles: headings (# ## ### ####), bullet/numbered lists, GitHub-style tables,
// fenced code blocks, inline `code`, **bold**, *italic*, blank lines.

const fs = require('fs');
const path = require('path');
const {
    Document, Packer, Paragraph, TextRun, Table, TableRow, TableCell,
    HeadingLevel, AlignmentType, LevelFormat, BorderStyle, WidthType, ShadingType,
    PageOrientation,
} = require('docx');

const inputPath  = path.join(__dirname, 'LPM_SIM_User_Guide.md');
const outputPath = path.join(__dirname, 'LPM_SIM_User_Guide.docx');

const md = fs.readFileSync(inputPath, 'utf8').split(/\r?\n/);

// -- Inline parser: returns TextRun[] from a line containing **bold**, *italic*, `code`
function inline(text, baseProps = {}) {
    const runs = [];
    let i = 0;
    while (i < text.length) {
        // Bold **
        if (text.startsWith('**', i)) {
            const end = text.indexOf('**', i + 2);
            if (end > i) {
                runs.push(new TextRun({ ...baseProps, text: text.slice(i + 2, end), bold: true }));
                i = end + 2; continue;
            }
        }
        // Inline code `…`
        if (text[i] === '`') {
            const end = text.indexOf('`', i + 1);
            if (end > i) {
                runs.push(new TextRun({ ...baseProps, text: text.slice(i + 1, end), font: 'Consolas', shading: { fill: 'F1F5F9', type: ShadingType.CLEAR } }));
                i = end + 1; continue;
            }
        }
        // Italic *…*  (avoid matching ** which we handle above)
        if (text[i] === '*' && text[i + 1] !== '*') {
            const end = text.indexOf('*', i + 1);
            if (end > i) {
                runs.push(new TextRun({ ...baseProps, text: text.slice(i + 1, end), italics: true }));
                i = end + 1; continue;
            }
        }
        // Plain char: collect until next special
        let next = text.length;
        for (let k = i + 1; k < text.length; k++) {
            const ch = text[k];
            if (ch === '`' || ch === '*') { next = k; break; }
        }
        runs.push(new TextRun({ ...baseProps, text: text.slice(i, next) }));
        i = next;
    }
    return runs;
}

// -- Build paragraph helpers
function p(children, opts = {}) {
    return new Paragraph({ children, spacing: { before: 60, after: 60 }, ...opts });
}
function heading(text, level) {
    const sizes = { 1: 32, 2: 28, 3: 24, 4: 22 };
    return new Paragraph({
        children: [new TextRun({ text, bold: true, color: '1F3A5F', size: sizes[level] || 22 })],
        spacing: { before: 280, after: 120 },
        heading: ['Heading1', 'Heading2', 'Heading3', 'Heading4'][level - 1],
    });
}
function bullet(text) {
    return new Paragraph({ children: inline(text), numbering: { reference: 'bullets', level: 0 }, spacing: { before: 30, after: 30 } });
}
function numbered(text) {
    return new Paragraph({ children: inline(text), numbering: { reference: 'numbers', level: 0 }, spacing: { before: 30, after: 30 } });
}
function codeLine(text) {
    return new Paragraph({
        children: [new TextRun({ text, font: 'Consolas', size: 18 })],
        spacing: { before: 0, after: 0 },
        shading: { fill: 'F1F5F9', type: ShadingType.CLEAR },
    });
}

const cellBorder = { style: BorderStyle.SINGLE, size: 4, color: 'CBD5E1' };
const cellBorders = { top: cellBorder, bottom: cellBorder, left: cellBorder, right: cellBorder };

function buildTable(rows) {
    if (rows.length === 0) return null;
    const colCount = rows[0].length;
    const fullWidth = 9000; // landscape content width fits ~13000; 9000 keeps tables readable in portrait too
    const colWidth = Math.floor(fullWidth / colCount);
    const trs = rows.map((cells, idx) => new TableRow({
        children: cells.map(text => new TableCell({
            borders: cellBorders,
            width: { size: colWidth, type: WidthType.DXA },
            shading: idx === 0 ? { fill: '0F172A', type: ShadingType.CLEAR } : undefined,
            margins: { top: 80, bottom: 80, left: 120, right: 120 },
            children: [new Paragraph({
                children: idx === 0
                    ? [new TextRun({ text: text.trim(), bold: true, color: 'FFFFFF', size: 18 })]
                    : inline(text.trim(), { size: 18 }),
            })],
        })),
        tableHeader: idx === 0,
    }));
    return new Table({
        width: { size: fullWidth, type: WidthType.DXA },
        columnWidths: Array(colCount).fill(colWidth),
        rows: trs,
    });
}

// -- Parse the markdown into docx blocks
const blocks = [];
let i = 0;
while (i < md.length) {
    const line = md[i];

    // Blank
    if (!line.trim()) {
        blocks.push(p([new TextRun('')]));
        i++; continue;
    }

    // Horizontal rule
    if (/^---+$/.test(line.trim())) {
        blocks.push(new Paragraph({
            children: [new TextRun({ text: '' })],
            border: { bottom: { style: BorderStyle.SINGLE, size: 6, color: '94A3B8', space: 1 } },
            spacing: { before: 120, after: 120 },
        }));
        i++; continue;
    }

    // Heading
    const hm = line.match(/^(#{1,4})\s+(.*)$/);
    if (hm) { blocks.push(heading(hm[2], hm[1].length)); i++; continue; }

    // Code fence
    if (/^```/.test(line)) {
        i++;
        while (i < md.length && !/^```/.test(md[i])) {
            blocks.push(codeLine(md[i] || ' '));
            i++;
        }
        i++; // skip closing fence
        continue;
    }

    // GitHub-style table: header line | with --- separator next
    if (line.includes('|') && i + 1 < md.length && /^[\s|:\-]+$/.test(md[i + 1]) && md[i + 1].includes('-')) {
        const tableRows = [];
        // Header row
        tableRows.push(line.split('|').slice(1, -1).map(s => s.trim()));
        i += 2; // skip header and separator
        while (i < md.length && md[i].includes('|')) {
            const cells = md[i].split('|').slice(1, -1).map(s => s.trim());
            if (cells.length > 0) tableRows.push(cells);
            i++;
        }
        const t = buildTable(tableRows);
        if (t) blocks.push(t);
        continue;
    }

    // Bullet list
    if (/^[-*]\s+/.test(line)) {
        blocks.push(bullet(line.replace(/^[-*]\s+/, '')));
        i++; continue;
    }

    // Numbered list
    if (/^\d+\.\s+/.test(line)) {
        blocks.push(numbered(line.replace(/^\d+\.\s+/, '')));
        i++; continue;
    }

    // Plain paragraph
    blocks.push(p(inline(line)));
    i++;
}

// -- Document
const doc = new Document({
    styles: {
        default: { document: { run: { font: 'Calibri', size: 22 } } },
    },
    numbering: {
        config: [
            { reference: 'bullets', levels: [{ level: 0, format: LevelFormat.BULLET, text: '•', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 270 } } } }] },
            { reference: 'numbers', levels: [{ level: 0, format: LevelFormat.DECIMAL, text: '%1.', alignment: AlignmentType.LEFT, style: { paragraph: { indent: { left: 540, hanging: 270 } } } }] },
        ],
    },
    sections: [{
        properties: {
            page: {
                size: { width: 12240, height: 15840 }, // US Letter
                margin: { top: 1080, right: 1080, bottom: 1080, left: 1080 },
            },
        },
        children: blocks,
    }],
});

Packer.toBuffer(doc).then(buf => {
    fs.writeFileSync(outputPath, buf);
    console.log('Wrote: ' + outputPath + ' (' + buf.length + ' bytes)');
});
