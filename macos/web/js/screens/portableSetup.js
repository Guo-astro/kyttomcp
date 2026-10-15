import { el, button, busyButton, sheet, definitionList } from '../dom.js';

export function setupActions(state) {
  const actions = el('div', 'setup-actions');
  if (!state.app?.capabilities?.includes('portable-setup')) return actions;
  actions.append(
    busyButton(state.setupBusy, 'Export setup…', 'Please wait…', 'export-setup', { className: 'button subtle' }),
    busyButton(state.setupBusy, 'Import setup…', 'Please wait…', 'preview-setup-import', { className: 'button subtle' }),
  );
  return actions;
}

export function renderPortableImport(state) {
  const current = state.sheet;
  const preview = current.preview;
  if (!preview) return null;
  const names = (preview.targetClients ?? []).map((id) => state.clients.find((client) => client.id === id)?.displayName ?? id);
  const body = [
    el('p', null, 'Review the destination and imported items before changing your client configurations.'),
    definitionList([
      ['Servers', preview.serverNames?.join(', ') || 'None'],
      ['Profiles', preview.profileNames?.join(', ') || 'None'],
      ['Destination clients', names.join(', ') || 'None'],
      ['Initial state', 'Disabled — enable each server after reviewing it'],
    ]),
  ];
  if (preview.pathDisplays?.length) {
    const destinations = el('section', 'detail-section');
    destinations.append(el('h3', null, 'Files to update'));
    const list = el('ul', 'selectable mono');
    preview.pathDisplays.forEach((path) => list.append(el('li', null, path)));
    destinations.append(list);
    body.push(destinations);
  }
  if (preview.pendingContractCount) body.push(el('p', 'field-hint', `${preview.pendingContractCount} imported contract${preview.pendingContractCount === 1 ? '' : 's'} will require a fresh health check and local review.`));
  for (const warning of preview.warnings ?? []) body.push(el('p', 'field-hint warning', warning));
  body.push(el('p', 'muted', 'Kytto backs up existing configurations. Imported definitions are disabled and no server runs during import.'));
  if (current.error) body.push(el('p', 'failure-message', current.error));
  return sheet('Import setup', body, [
    button('Cancel', 'close-sheet', { className: 'button subtle' }),
    busyButton(current.busy, 'Import disabled', 'Importing…', 'apply-setup-import', { className: 'button primary' }),
  ]);
}
