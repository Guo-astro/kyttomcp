// Fixed browser destinations (§7.14), shared by all error states. The native
// navigation delegate opens these outside the application; no IPC is needed.
import { el } from './dom.js';

const BUG_REPORT_URL = 'https://kytto.jakubhecht.sk/#report-bug';
const GITHUB_ISSUE_URL = 'https://github.com/heyitsjakub/KyttoMCP/issues/new/choose';

export function bugReportLink({ github = false, className = 'button subtle' } = {}) {
  const link = el('a', `${className} support-link`, github ? 'Report on GitHub…' : 'Report a bug…');
  link.href = github ? GITHUB_ISSUE_URL : BUG_REPORT_URL;
  link.target = '_blank';
  link.rel = 'noopener noreferrer';
  link.title = github ? 'Open a public GitHub issue in your browser' : 'Open the private bug report form in your browser';
  return link;
}
