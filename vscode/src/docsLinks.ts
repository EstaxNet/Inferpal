// Where the documentation is: the docs folder of the public repository, at the tag of the version installed — the
// pages on master describe what is developed since. A version that is not a release number (a development build,
// "0.0.0-dev") reads master. The same address as the Core's (Inferpal.Core/Services/DocsLinks.cs), held to it by a test.

export const repository = 'https://github.com/EstaxNet/Inferpal';

/** `page` of the docs folder, for `version`. */
export function docsUrl(version: string | undefined, page = 'README.md'): string {
  const ref = version && /^\d+\.\d+\.\d+$/.test(version) ? `v${version}` : 'master';
  return `${repository}/blob/${ref}/docs/${page}`;
}
