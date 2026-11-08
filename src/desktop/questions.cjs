const { lstatSync, readFileSync } = require('node:fs');
const { randomUUID } = require('node:crypto');

const maxQuestionBytes = 24 * 1024;

function readQuestion(file) {
  const info = lstatSync(file, { throwIfNoEntry: false });
  if (!info) return null;
  if (!info.isFile() || info.isSymbolicLink() || info.size > maxQuestionBytes) throw new Error('The agent question must be a JSON file of at most 24 KiB.');
  let data;
  try {
    const bytes = readFileSync(file);
    if (bytes.length > maxQuestionBytes) throw new Error('Question too large.');
    data = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch { throw new Error('The agent question must contain valid UTF-8 JSON.'); }
  if (!data || Array.isArray(data) || typeof data.question !== 'string' || !data.question.trim() || data.question.length > 4000) {
    throw new Error('The agent must provide a question of at most 4000 characters.');
  }
  if (data.options !== undefined && (!Array.isArray(data.options) || data.options.length > 6
    || data.options.some(option => typeof option !== 'string' || !option.trim() || option.length > 300))) {
    throw new Error('An agent question accepts up to six short text options.');
  }
  if (data.context !== undefined && (typeof data.context !== 'string' || data.context.length > 8000)) {
    throw new Error('The question checkpoint must be text of at most 8000 characters.');
  }
  return { id: randomUUID(), question: data.question.trim(), options: data.options || [],
    context: data.context || '', askedAt: new Date().toISOString() };
}

function validateAnswer(question, id, text) {
  if (!question || question.id !== id) throw new Error('This question is no longer awaiting an answer.');
  if (typeof text !== 'string' || !text.trim()) throw new Error('Enter an answer.');
  if (Buffer.byteLength(text, 'utf8') > 16 * 1024) throw new Error('Answers are limited to 16 KiB.');
  return text;
}

module.exports = { readQuestion, validateAnswer };
