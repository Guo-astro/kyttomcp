import assert from 'node:assert/strict';
import test from 'node:test';
import { setState, getState, matrixClients, visibleServers, scopeOptions } from '../state.js';
import { toggle } from '../screens/serverRow.js';

const clients = [
  { id: 'code', displayName: 'Claude Code', iconAsset: 'code', state: 'ready' },
  { id: 'absent', displayName: 'Other client', iconAsset: 'other', state: 'notInstalled' },
  { id: 'workspace-a', displayName: 'Project A', iconAsset: 'code', configurationScope: 'workspace', isReadOnly: true },
  { id: 'workspace-b', displayName: 'Project B', iconAsset: 'code', configurationScope: 'workspace', isReadOnly: true },
];
const server = (id, enabledIn, kind = 'config') => ({ id, name: id, commandSummary: `node ${id}`, enabledIn, source: { kind }, isBundled: kind === 'plugin', health: null });
const servers = [
  server('global', { code: 'enabled' }),
  server('project-a', { 'workspace-a': 'enabled' }, 'custom'),
  server('project-b', { 'workspace-b': 'enabled' }, 'custom'),
  server('plugin', { code: 'enabled' }, 'plugin'),
];

function reset(extra = {}) {
  setState({ clients, servers, filter: '', sourceFilter: 'all', scopeFilter: 'all', settings: { showsCustomSources: true }, selectedServerId: null, ...extra });
}

test('the default matrix retains absent clients and folding sources removes source-only rows', () => {
  reset();
  assert.deepEqual(matrixClients().map((client) => client.id), ['code', 'absent', 'workspace-a', 'workspace-b']);
  setState({ settings: { showsCustomSources: false } });
  assert.deepEqual(matrixClients().map((client) => client.id), ['code', 'absent']);
  assert.deepEqual(visibleServers().map((entry) => entry.id), ['global', 'plugin']);
});

test('an explicitly selected workspace shows its owning global client and inherited servers', () => {
  reset({ scopeFilter: 'workspace-a' });
  assert.deepEqual(matrixClients().map((client) => client.id), ['code', 'workspace-a']);
  assert.deepEqual(visibleServers().map((entry) => entry.id), ['global', 'project-a', 'plugin']);
  setState({ settings: { showsCustomSources: false } });
  assert.deepEqual(matrixClients().map((client) => client.id), ['code', 'workspace-a']);
});

test('source and text filters compose without including another workspace', () => {
  reset({ scopeFilter: 'workspace-a', sourceFilter: 'custom' });
  assert.deepEqual(visibleServers().map((entry) => entry.id), ['project-a']);
  setState({ sourceFilter: 'plugin' });
  assert.deepEqual(visibleServers().map((entry) => entry.id), ['plugin']);
  setState({ filter: 'not-present' });
  assert.deepEqual(visibleServers(), []);
});

test('refresh preserves inspector selection and removed subjects reset safely', () => {
  reset({ scopeFilter: 'workspace-a', selectedServerId: 'project-a' });
  setState({ servers: [...servers] });
  assert.equal(getState().selectedServerId, 'project-a');
  setState({ clients: clients.filter((client) => client.id !== 'workspace-a'), servers: servers.filter((entry) => entry.id !== 'project-a') });
  assert.equal(getState().scopeFilter, 'all');
  assert.equal(getState().selectedServerId, null);
  assert.equal(scopeOptions().some((entry) => entry.id === 'workspace-a'), false);
});

test('installed plugin controls cannot write or copy while extension owner controls remain available', () => {
  const previous = globalThis.document;
  const node = () => ({ dataset: {}, children: [], classList: { add() {} }, setAttribute() {}, append(...children) { this.children.push(...children); } });
  globalThis.document = { createElement: node, createElementNS: node };
  try {
    const state = { busyCells: new Set() };
    const owner = { id: 'code', displayName: 'Client', state: 'ready' };
    const plugin = { id: 'plugin', name: 'Plugin', originKind: 'plugin', isBundled: true };
    assert.equal(toggle(state, plugin, owner, 'enabled', false).disabled, true);
    assert.equal(toggle(state, plugin, owner, 'absent', false).disabled, true);
    const extension = { id: 'extension', name: 'Extension', originKind: 'extension', isBundled: true, source: { kind: 'extension', readOnly: false } };
    assert.notEqual(toggle(state, extension, owner, 'enabled', false).disabled, true);
    assert.equal(toggle(state, extension, owner, 'absent', false).disabled, true);
  } finally {
    if (previous === undefined) delete globalThis.document;
    else globalThis.document = previous;
  }
});
