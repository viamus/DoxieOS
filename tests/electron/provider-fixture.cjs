const { readFileSync, writeFileSync } = require('node:fs');
const { join } = require('node:path');
const assert = require('node:assert/strict');

const published = '{ "buildId": 9007199254740993, "note": "Preserve this context" }\n';
const section = readFileSync(0, 'utf8').split('## Doxie request\n\n')[1];
const request = JSON.parse(section.split('\n').slice(1).join('\n').split('\n```')[0]);
const args = process.argv.slice(2);
const model = args[args.indexOf('--model') + 1];
assert.ok(args.includes('--ephemeral'));
assert.equal(request.schema, 'doxie.agent-request.v1');
assert.equal(request.handoff.schema, 'doxie.workflow-handoff.v1');
if (request.handoff.nodeId === 'producer') {
  assert.equal(model, 'doxie/producer-v1');
  assert.deepEqual(request.handoff.sources, []);
  writeFileSync(process.env.DOXIE_WORKFLOW_CONTEXT_OUT, published);
} else {
  assert.equal(model, 'doxie/consumer-v2');
  assert.equal(request.handoff.sources[0].nodeId, 'producer');
  assert.equal(request.handoff.sources[0].contextJson, published);
  writeFileSync(join(request.outputDirectory, 'result.json'), request.handoff.sources[0].contextJson);
}
console.log(JSON.stringify({ type: 'item.completed', item: { type: 'agent_message', text: 'Step complete' } }));
