import { readFileSync } from 'node:fs';

const registry = 'https://registry.npmjs.org';
const lockfile = JSON.parse(readFileSync(new URL('../package-lock.json', import.meta.url), 'utf8'));
const manifest = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'));

if (lockfile.lockfileVersion !== 3 || !lockfile.packages?.['']) {
  throw new Error('Expected an npm v3 lockfile with a root package.');
}

const packages = Object.entries(lockfile.packages).filter(([location]) => location !== '');
if (!packages.length) throw new Error('The npm lockfile has no locked dependencies.');

function checkVersionSpec(location, name, spec) {
  if (typeof spec !== 'string' || !spec.trim() || spec.trim().startsWith('.') || /[:/\\]/.test(spec)) {
    throw new Error(`${location} may only declare npm registry versions for ${name}.`);
  }
}

function checkDependencySpecs(location, dependency) {
  for (const group of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']) {
    for (const [name, spec] of Object.entries(dependency[group] ?? {})) {
      checkVersionSpec(location, name, spec);
    }
  }
}

function checkOverrides(overrides) {
  for (const [name, spec] of Object.entries(overrides)) {
    if (spec && typeof spec === 'object' && !Array.isArray(spec)) checkOverrides(spec);
    else checkVersionSpec('package.json overrides', name, spec);
  }
}

function checkPackage(location, dependency) {
  if (!location.startsWith('node_modules/') || dependency.link || dependency.inBundle || typeof dependency.version !== 'string') {
    throw new Error(`${location} must resolve to a versioned npm registry package.`);
  }

  const packageName = dependency.name ?? location.split('node_modules/').at(-1);
  const tarballName = `${packageName.split('/').at(-1)}-${dependency.version}.tgz`;
  const tarball = URL.parse(dependency.resolved ?? '');
  if (tarball?.origin !== registry || tarball.username || tarball.password || tarball.search || tarball.hash
    || decodeURIComponent(tarball.pathname) !== `/${packageName}/-/${tarballName}`) {
    throw new Error(`${location} must use a tarball from ${registry}.`);
  }
  if (!/^sha512-[A-Za-z0-9+/]{86}==$/.test(dependency.integrity ?? '')) {
    throw new Error(`${location} requires a SHA-512 integrity digest.`);
  }
  checkDependencySpecs(location, dependency);
}

checkDependencySpecs('package.json', manifest);
checkDependencySpecs('package-lock.json', lockfile.packages['']);
checkOverrides(manifest.overrides ?? {});
for (const [location, dependency] of packages) checkPackage(location, dependency);
console.log(`Verified npm registry sources and SHA-512 digests for ${packages.length} locked dependencies.`);
