// The published Windows Node-API addon imports node.exe directly and cannot be
// loaded by Electron. The official Node.js WASM build uses the same database
// format and the host filesystem, without a separate server or Node runtime.
const useWasm = Boolean(process.versions.electron) || process.env.DOXIE_BRAIN_RUNTIME === 'wasm';
let engine;
let initializing;

async function initializeMemoryRuntime() {
  if (!useWasm || engine) return;
  if (!initializing) {
    initializing = (async () => {
      const wasm = require('@ladybugdb/wasm-core/nodejs/sync');
      await wasm.init();
      engine = { implementation: wasm, wasm: true };
    })().catch(error => { initializing = null; throw error; });
  }
  await initializing;
}

function implementation() {
  if (!engine) {
    if (useWasm) throw new Error('Initialize the Brain runtime before opening its database.');
    // Delay native loading until an actual database is created: MCP clients
    // proxied to the desktop never need to load a database implementation.
    engine = { implementation: require(`@ladybugdb/core-${process.platform}-${process.arch}`), wasm: false };
  }
  return engine;
}

class Database {
  constructor(...args) {
    this.engine = implementation();
    this.value = new this.engine.implementation.Database(...args);
  }
  closeSync() { return this.engine.wasm ? this.value.close() : this.value.closeSync(); }
}

class Connection {
  constructor(database, ...args) {
    this.engine = database.engine;
    this.value = new this.engine.implementation.Connection(database.value, ...args);
  }
  prepareSync(statement) { return this.engine.wasm ? this.value.prepare(statement) : this.value.prepareSync(statement); }
  result(value) {
    if (!this.engine.wasm) return value;
    if (!value.isSuccess()) {
      const message = value.getErrorMessage();
      value.close();
      throw new Error(message);
    }
    return { getAllSync: () => value.getAllObjects(), close: () => value.close() };
  }
  querySync(statement) { return this.result(this.engine.wasm ? this.value.query(statement) : this.value.querySync(statement)); }
  executeSync(statement, parameters) { return this.result(this.engine.wasm ? this.value.execute(statement, parameters) : this.value.executeSync(statement, parameters)); }
  closeSync() { return this.engine.wasm ? this.value.close() : this.value.closeSync(); }
}

module.exports = { Database, Connection, initializeMemoryRuntime };
