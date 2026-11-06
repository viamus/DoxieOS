const assert = require('node:assert/strict');
const { mkdtemp, mkdir, readFile, rm, writeFile } = require('node:fs/promises');
const { tmpdir } = require('node:os');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const { setTimeout: delay } = require('node:timers/promises');
const test = require('node:test');
const { createWorkflows, parseCron, nextCron } = require('../../src/desktop/workflows.cjs');

function node(id, kind, properties = {}) {
  return { id, kind, label: id, x: 0, y: 0, ...properties };
}

function edge(fromNodeId, toNodeId, condition) {
  return { fromNodeId, toNodeId, condition };
}

function workflow(nodes, edges, properties = {}) {
  return { id: 'sample', name: 'Sample workflow', trigger: { kind: 'manual' }, nodes, edges, ...properties };
}

async function fixture(t, definition, dispatch, initialize) {
  const root = await mkdtemp(path.join(tmpdir(), 'doxie-workflow-'));
  const workspace = { id: 'target', path: path.join(root, 'target') };
  await mkdir(workspace.path);
  const db = new DatabaseSync(path.join(root, 'state.db'));
  const runs = path.join(root, '.runs');
  const launched = [];
  const notifications = [];
  const jobs = new Map();
  const storage = {
    root, roots: { runs }, db,
    get(kind, id) {
      if (kind === 'workflows' && id === definition.id) return definition;
      if (kind === 'workspaces' && id === workspace.id) return workspace;
      if (kind === 'agents') return { id, name: id, modes: { run: { argumentsTemplate: '{name}' } } };
      return null;
    },
    list: kind => kind === 'workflows' ? [definition] : [],
  };
  const agents = {
    start(id, params) {
      const agentRunId = String(launched.push({ id, params }));
      const controller = new AbortController();
      const task = Promise.resolve().then(() => dispatch?.(id, params, controller.signal));
      jobs.set(agentRunId, { task, controller });
      return { id: agentRunId, providerId: params.providerId ?? 'claude-code', model: params.model ?? 'default-model' };
    },
    async wait(id) {
      const job = jobs.get(id);
      return await job.task ?? { status: job.controller.signal.aborted ? 'Cancelled' : 'Completed', output: [] };
    },
    stop(id) { jobs.get(id)?.controller.abort(); },
  };
  initialize?.(db);
  const engine = createWorkflows({ storage, agents, notify: notification => notifications.push(notification) });
  t.after(async () => {
    await engine.close();
    db.close();
    await rm(root, { force: true, recursive: true });
  });
  return { engine, storage, agents, db, launched, notifications, runs, workspace };
}

async function until(predicate) {
  for (let attempt = 0; attempt < 200; attempt++) {
    if (predicate()) return;
    await delay(5);
  }
  assert.fail('Timed out waiting for workflow state');
}

test('runs decisions, approval and workspace delivery, then reuses successful upstream artifacts on rerun', async t => {
  const fixtureValue = 'sensitive-example';
  const definition = workflow([
    node('trigger', 'Trigger'), node('producer', 'agent', { agentId: 'producer', workspaceId: '{{trigger.workspace}}', inputs: { name: '{{trigger.Name}}' } }),
    node('decision', 5, { inputs: { path: 'check.passed', operator: 'equals', value: 'true' } }),
    node('approval', 'agent', { agentId: 'approval-gate', inputs: { prompt: 'Review {{trigger.Name}}' } }),
    node('unselected', 'agent', { agentId: 'unselected' }),
    node('aggregate', 'agent', { agentId: 'aggregate' }),
    node('deliver', 'output', { outputWorkspaceId: '{{trigger.workspace}}', outputFileName: 'report.md' }),
  ], [edge('trigger', 'producer'), edge('producer', 'decision'), edge('decision', 'approval', 'yes'),
    edge('decision', 'unselected', 'else'), edge('approval', 'aggregate'), edge('aggregate', 'deliver')], {
    trigger: { kind: 'manual', inputs: [{ id: 'name', required: true }, { id: 'workspace', defaultValue: 'target' }] },
    env: { ACCESS_TOKEN: fixtureValue },
  });
  const { engine, workspace, launched, notifications, runs, storage, agents } = await fixture(t, definition, async (id, params) => {
    assert.equal(id, 'producer');
    assert.equal(params.arguments, 'Quarterly review');
    assert.equal(params.cwd, workspace.path);
    assert.equal(params.env.ACCESS_TOKEN, fixtureValue);
    await writeFile(path.join(params.env.DOXIE_WORKFLOW_OUTPUT_DIR, 'result.json'), '{"check":{"passed":true}}');
    params.onOutput({ source: 'Stdout', text: 'Provider streamed a line' });
    return { status: 'Completed', output: [{ source: 'Stdout', text: 'Provider streamed a line' }] };
  });
  assert.throws(() => engine.run(definition.id, {}), /Missing required trigger input/);
  const run = engine.run(definition.id, { triggerInputs: { NAME: 'Quarterly review' } });
  await until(() => engine.getRun(run.id).status === 'AwaitingApproval');
  assert.equal(engine.approve(run.id, 'approval', { approve: true, comment: 'Proceed with care' }), true);
  assert.equal(engine.approve(run.id, 'approval', true), false);
  const result = await engine.wait(run.id);
  assert.equal(result.status, 'Succeeded');
  assert.equal(result.nodeRuns.unselected.status, 'Skipped');
  assert.equal(result.nodeRuns.producer.logs.filter(line => line === 'Provider streamed a line').length, 1);
  assert(result.nodeRuns.producer.logs.includes('env: ACCESS_TOKEN=***'));
  assert(!JSON.stringify(result).includes(fixtureValue));
  assert.match(await readFile(path.join(workspace.path, 'report.md'), 'utf8'), /Proceed with care/);
  assert.equal(JSON.parse(await readFile(path.join(runs, run.id, 'decision', 'decision.json'), 'utf8')).selectedBranch, 'true');
  assert.equal(notifications.length, 2);
  assert.equal(notifications[1].contentFormat, 'markdown');
  assert.match(notifications[1].content, /Proceed with care/);

  const replay = engine.rerun(run.id, 'approval', 'Keep previous output');
  await until(() => engine.getRun(replay.id).status === 'AwaitingApproval');
  engine.approve(replay.id, 'approval', true);
  assert.equal((await engine.wait(replay.id)).status, 'Succeeded');
  assert.equal(launched.length, 1);
  const hydrated = createWorkflows({ storage, agents });
  assert.equal(hydrated.getRun(run.id).nodeRuns.approval.outputSummary, 'approved');
  assert.equal(hydrated.listRuns('SAMPLE').length, 2);
  await hydrated.close();
});

test('executes loop bodies in iteration folders, bounds concurrency and excludes failed iterations in continue mode', async t => {
  const definition = workflow([
    node('trigger', 'trigger'), node('producer', 'agent', { agentId: 'producer' }),
    node('iterate', 'agent', { agentId: 'loop', inputs: { array_path: 'items', concurrency: '2', on_failure: 'continue' } }),
    node('item', 'agent', { agentId: 'worker', loopId: 'iterate', inputs: { name: '{{loop.item.Name}}/{{loop.index}}/{{loop.total}}/{{loop.item.large}}' } }),
    node('join', 'aggregate'),
  ], [edge('trigger', 'producer'), edge('producer', 'iterate'), edge('iterate', 'item'), edge('item', 'join')]);
  let active = 0;
  let peak = 0;
  const { engine, runs } = await fixture(t, definition, async (id, params) => {
    const output = params.env.DOXIE_WORKFLOW_OUTPUT_DIR;
    if (id === 'producer') {
      await writeFile(path.join(output, 'result.json'), '{"items":[{"name":"a"},{"name":"b"},{"name":"c","large":900719925474099312345}]}');
      return;
    }
    const index = Number(params.env.LOOP_INDEX);
    const itemJson = await readFile(path.join(params.env.DOXIE_WORKFLOW_INPUT_DIRS, 'item.json'), 'utf8');
    const item = JSON.parse(itemJson);
    assert.match(params.arguments, new RegExp(`^${item.name}/${index}/3`));
    assert.equal(params.env.LOOP_ITEM, itemJson);
    if (index === 2) assert.equal(params.arguments, 'c/2/3/900719925474099312345');
    active++;
    peak = Math.max(peak, active);
    await delay(15);
    active--;
    if (index === 1) return { status: 'Failed' };
    await writeFile(path.join(output, 'output.md'), item.name);
  });
  const run = await engine.wait(engine.run(definition.id).id);
  assert.equal(run.status, 'Succeeded');
  assert.equal(run.nodeRuns['item#iter-1'].status, 'Failed');
  assert.equal(run.nodeRuns.item.status, 'Succeeded');
  assert.equal(run.nodeRuns.join.status, 'Succeeded');
  assert.equal(peak, 2);
  const results = JSON.parse(await readFile(path.join(runs, run.id, 'iterate', 'results.json'), 'utf8'));
  assert.deepEqual(results.map(result => result.index), [0, 2]);
  assert.match(await readFile(path.join(runs, run.id, 'join', 'merged.md'), 'utf8'), /from: results.json/);
});

test('rejecting approval cancels sibling provider runs and skips dependent nodes', async t => {
  const definition = workflow([
    node('approval', 'agent', { agentId: 'approval-gate' }),
    node('sibling', 'agent', { agentId: 'blocking' }),
    node('downstream', 'aggregate'),
  ], [edge('approval', 'downstream')]);
  let started = false;
  let cancelled = false;
  const { engine } = await fixture(t, definition, async (id, params, signal) => {
    started = true;
    try { await delay(60_000, null, { signal }); }
    catch (error) {
      if (error.name !== 'AbortError') throw error;
      cancelled = true;
    }
  });
  const run = engine.run(definition.id);
  await until(() => started && engine.getRun(run.id).status === 'AwaitingApproval');
  assert.equal(engine.approve(run.id, 'approval', { approve: false, comment: 'Changes required' }), true);
  const result = await engine.wait(run.id);
  assert.equal(result.status, 'Cancelled');
  assert.equal(result.nodeRuns.approval.status, 'Failed');
  assert.match(result.nodeRuns.approval.outputSummary, /Changes required/);
  assert.equal(result.nodeRuns.downstream.status, 'Skipped');
  assert.equal(cancelled, true);
});

test('cron anchors first sighting, defers a due schedule while the previous run is active, and ignores disabled schedules', async t => {
  const definition = workflow([node('trigger', 'trigger')], [], { trigger: { kind: 'cron', cronExpression: '*/5 * * * *' } });
  const { engine } = await fixture(t, definition);
  const at = minute => new Date(`2026-11-05T00:${String(minute).padStart(2, '0')}:00Z`);
  engine.tick(at(0));
  assert.equal(engine.listRuns().length, 0);
  engine.tick(at(5));
  const [first] = engine.listRuns();
  assert.equal(first.triggeredBy, 'cron:*/5 * * * *');
  engine.tick(at(10));
  assert.equal(engine.listRuns().length, 1);
  await engine.wait(first.id);
  engine.tick(at(11));
  assert.equal(engine.listRuns().length, 2);
  definition.enabled = false;
  engine.tick(at(15));
  assert.equal(engine.listRuns().length, 2);
  assert.equal(parseCron('0 9 * nov thu')(new Date('2026-11-05T09:00:00Z')), true);
  assert.equal(parseCron('60 * * * *'), null);
  assert.equal(parseCron('*/0 * * * *'), null);
  assert.equal(nextCron('0 9 5 nov thu', new Date('2026-11-05T09:00:00Z')).toISOString(), '2037-11-05T09:00:00.000Z');
});

test('preserves legacy SQLite history and closes abandoned approval runs on startup', async t => {
  const definition = workflow([node('trigger', 'trigger')], []);
  const { engine, db } = await fixture(t, definition, undefined, database => {
    database.exec(`
      CREATE TABLE workflow_runs (id TEXT PRIMARY KEY, workflow_id TEXT, triggered_by TEXT, started_at TEXT, finished_at TEXT, status TEXT);
      CREATE TABLE workflow_node_runs (run_id TEXT, node_id TEXT, status TEXT, started_at TEXT, finished_at TEXT, output_summary TEXT, logs TEXT, PRIMARY KEY (run_id, node_id));
      INSERT INTO workflow_runs VALUES ('legacy', 'sample', 'manual:api', '2026-11-05T18:00:00.0000000+00:00', '2026-11-05T18:01:00.0000000+00:00', 'Succeeded');
      INSERT INTO workflow_runs VALUES ('waiting', 'sample', 'manual:api', '2026-11-05T18:02:00.0000000+00:00', NULL, 'AwaitingApproval');
      INSERT INTO workflow_node_runs VALUES ('legacy', 'trigger', 'Succeeded', NULL, NULL, 'done', 'line 1' || char(30) || 'line 2');
      INSERT INTO workflow_node_runs VALUES ('waiting', 'approval', 'AwaitingApproval', NULL, NULL, NULL, 'waiting');
    `);
  });
  assert.deepEqual(engine.getRun('legacy').nodeRuns.trigger.logs, ['line 1', 'line 2']);
  assert.deepEqual(engine.getRun('legacy').triggerInputs, {});
  assert.equal(engine.getRun('waiting').status, 'Cancelled');
  assert.equal(engine.getRun('waiting').nodeRuns.approval.status, 'Skipped');
  assert.equal(db.prepare("SELECT status FROM workflow_runs WHERE id='waiting'").get().status, 'Cancelled');
  assert.equal(engine.clearHistory(), 2);
  assert.equal(engine.listRuns().length, 0);
});

test('rejects cycles and prevents a workflow from writing outside the workspace', async t => {
  const definition = workflow([node('trigger', 'trigger'), node('output', 'output', {
    outputWorkspaceId: 'target', outputFileName: '../escaped.md',
  })], [edge('trigger', 'output'), edge('output', 'trigger')]);
  const { engine, storage } = await fixture(t, definition);
  assert.throws(() => engine.run(definition.id), /dependency cycle/);
  definition.edges.pop();
  const result = await engine.wait(engine.run(definition.id).id);
  assert.equal(result.status, 'Failed');
  assert.match(result.nodeRuns.output.logs.at(-1), /Unsafe relative file path/);
  await assert.rejects(readFile(path.join(storage.root, 'escaped.md'), 'utf8'), { code: 'ENOENT' });
});

test('transfers exact context through native steps, gives agents isolated direct inputs and refuses expired replay context', async t => {
  const definition = workflow([
    node('produce', 'Agent', { agentId: 'producer', execution: { providerId: 'codex', model: 'gpt-test' } }),
    node('publish-file', 'Agent', { agentId: 'files' }),
    node('decide', 'Tool', { toolId: 'if-else', inputs: { source: 'context', path: 'route', operator: 'equals', value: 'true' } }),
    node('review', 'Tool', { toolId: 'approval-gate' }),
    node('combine', 'Tool', { toolId: 'aggregate' }),
    node('consume', 'Agent', { agentId: 'consumer' }),
    node('unselected', 'Agent', { agentId: 'never' }),
  ], [edge('produce', 'decide'), edge('decide', 'review', 'true'), edge('decide', 'unselected', 'false'),
    edge('review', 'combine'), edge('publish-file', 'combine'), edge('combine', 'consume')]);
  const contextJson = ' { "large": 900719925474099312345, "route": true, "value": "secret-only-context" }\n';
  let inputSnapshot;
  const { engine, db, runs, launched } = await fixture(t, definition, async (id, params) => {
    const output = params.env.DOXIE_WORKFLOW_OUTPUT_DIR;
    if (id === 'producer') {
      assert.equal(params.providerId, 'codex');
      assert.equal(params.model, 'gpt-test');
      assert.deepEqual(params.handoff.sources, []);
      await writeFile(params.contextOutputPath, contextJson);
      params.onOutput({ source: 'Stdout', text: 'provider diagnostic only' });
    } else if (id === 'files') {
      await writeFile(path.join(output, 'report.md'), 'explicit business artifact');
      await writeFile(path.join(output, 'unlisted.log'), 'excluded diagnostics');
      await writeFile(path.join(output, '.doxie-artifact.json'), '{"files":["report.md"]}');
    } else {
      assert.equal(id, 'consumer');
      assert.equal(params.handoff.schema, 'doxie.workflow-handoff.v1');
      assert.equal(params.handoff.nodeId, 'consume');
      const [source] = params.handoff.sources;
      assert.deepEqual(params.handoff.sources.map(item => item.nodeId), ['combine']);
      assert.deepEqual(source.forwardedContexts, [{ nodeId: 'produce', contextJson }]);
      assert.deepEqual(source.artifacts.map(artifact => [artifact.relativePath, artifact.mediaType]), [['merged.md', 'text/markdown']]);
      inputSnapshot = source.artifactDirectory;
      assert.equal(params.env.DOXIE_WORKFLOW_INPUT_DIRS, inputSnapshot);
      const file = path.join(inputSnapshot, 'merged.md');
      const report = await readFile(file, 'utf8');
      assert.match(report, /explicit business artifact/);
      assert.doesNotMatch(report, /secret-only-context|provider diagnostic only|excluded diagnostics/);
      await writeFile(file, 'mutation of the copy only');
      await writeFile(path.join(output, 'result.json'), '{"ok":true}');
    }
  });

  const initial = engine.run(definition.id);
  await until(() => engine.getRun(initial.id).status === 'AwaitingApproval');
  assert.equal(engine.approve(initial.id, 'review', true), true);
  const completed = await engine.wait(initial.id);
  assert.equal(completed.status, 'Succeeded');
  assert.deepEqual(launched.map(item => item.id).sort(), ['consumer', 'files', 'producer']);
  assert.equal(completed.nodeRuns.produce.providerId, 'codex');
  assert.equal(completed.nodeRuns.produce.model, 'gpt-test');
  assert.equal(completed.nodeRuns.produce.hasTransientContext, true);
  assert.equal(completed.nodeRuns.review.providerId, null);
  assert.equal(completed.nodeRuns.combine.hasTransientContext, true);
  assert.equal(db.prepare("SELECT has_transient_context FROM workflow_node_runs WHERE run_id=? AND node_id='combine'").get(initial.id).has_transient_context, 1);
  assert.doesNotMatch(JSON.stringify(completed), /secret-only-context/);
  await assert.rejects(readFile(path.join(runs, initial.id, 'produce', 'output.md')), { code: 'ENOENT' });
  await assert.rejects(readFile(path.join(inputSnapshot, 'merged.md')), { code: 'ENOENT' });
  assert.match(await readFile(path.join(runs, initial.id, 'combine', 'merged.md'), 'utf8'), /explicit business artifact/);
  assert.equal(await readFile(path.join(runs, initial.id, 'consume', 'result.json'), 'utf8'), '{"ok":true}');

  const replay = await engine.wait(engine.rerun(initial.id, 'consume').id);
  assert.equal(replay.status, 'Failed');
  assert.match(replay.nodeRuns.consume.logs.at(-1), /prior context for node 'combine' expired/);
  assert.equal(launched.length, 3);
});

test('rejects malformed or oversized transient context and removes the transfer files', async t => {
  const definition = workflow([node('source', 'Agent', { agentId: 'producer' })], []);
  for (const [name, content, error] of [
    ['invalid JSON', '{"do-not-persist":', /valid UTF-8 JSON/],
    ['non-object JSON', '[1,2,3]', /valid UTF-8 JSON object/],
    ['oversized JSON', JSON.stringify({ data: 'x'.repeat(65536) }), /at most 64 KiB/],
  ]) {
    await t.test(name, async subtest => {
      let output;
      const { engine, runs } = await fixture(subtest, definition, async (id, params) => {
        output = params.contextOutputPath;
        await writeFile(output, content);
      });
      const result = await engine.wait(engine.run(definition.id).id);
      assert.equal(result.status, 'Failed');
      assert.match(result.nodeRuns.source.logs.at(-1), error);
      assert.doesNotMatch(JSON.stringify(result), /do-not-persist/);
      await assert.rejects(readFile(output), { code: 'ENOENT' });
      await assert.rejects(readFile(path.join(runs, result.id, 'source', 'output.md')), { code: 'ENOENT' });
    });
  }
});

test('keeps transient loop items out of durable decision diagnostics and files', async t => {
  const transientValue = 'loop-value-that-must-expire';
  const definition = workflow([
    node('produce', 'Agent', { agentId: 'producer' }),
    node('iterate', 'Tool', { toolId: 'loop', inputs: { array_source: 'context', array_path: 'items' } }),
    node('check', 'Tool', { loopId: 'iterate', toolId: 'if-else', inputs: {
      source: 'item.json', path: 'value', operator: 'equals', value: '{{loop.item.value}}',
    } }),
    node('consume', 'Agent', { agentId: 'consumer', loopId: 'iterate' }),
    node('finish', 'Tool', { toolId: 'aggregate' }),
  ], [edge('produce', 'iterate'), edge('iterate', 'check'), edge('check', 'consume', 'true'), edge('consume', 'finish')]);
  let itemSnapshot;
  const { engine, runs, launched } = await fixture(t, definition, async (id, params) => {
    if (id === 'producer') {
      await writeFile(params.contextOutputPath, JSON.stringify({ items: [{ value: transientValue }] }));
      return;
    }
    const [decision, loopItem] = params.handoff.sources;
    assert.equal(decision.nodeId, 'check');
    assert.deepEqual(decision.artifacts.map(artifact => artifact.relativePath), ['decision.json']);
    assert.equal(loopItem.kind, 'loop-item');
    assert.equal(params.env.DOXIE_WORKFLOW_INPUT_DIRS, decision.artifactDirectory);
    itemSnapshot = path.join(loopItem.artifactDirectory, 'item.json');
    assert.deepEqual(JSON.parse(await readFile(itemSnapshot, 'utf8')), { value: transientValue });
    assert.equal(JSON.parse(params.env.LOOP_ITEM).value, transientValue);
  });
  const run = await engine.wait(engine.run(definition.id).id);
  assert.equal(run.status, 'Succeeded');
  assert.deepEqual(launched.map(entry => entry.id), ['producer', 'consumer']);
  const decisionFolder = path.join(runs, run.id, 'iterate', 'iter-0', 'check');
  const decision = JSON.parse(await readFile(path.join(decisionFolder, 'decision.json'), 'utf8'));
  assert.equal(decision.selectedBranch, 'true');
  assert.equal(decision.matched, true);
  assert(!Object.hasOwn(decision, 'actual'));
  assert(!Object.hasOwn(decision, 'expected'));
  assert(!JSON.stringify({ run, decision }).includes(transientValue));
  await assert.rejects(readFile(path.join(decisionFolder, 'item.json')), { code: 'ENOENT' });
  await assert.rejects(readFile(itemSnapshot), { code: 'ENOENT' });
});
