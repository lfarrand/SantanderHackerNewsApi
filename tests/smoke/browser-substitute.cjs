const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const scenario = process.argv[2];
const directory = process.env.HN_BROWSER_RESULTS;
console.log('controlled browser stdout');
console.error('controlled browser stderr');
if (scenario === 'browser-timeout') {
  const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'], { stdio: 'ignore' });
  fs.writeFileSync(path.join(directory, 'owned-pids.json'), JSON.stringify([process.pid, child.pid]));
  setInterval(() => {}, 1000);
} else if (scenario === 'browser-failure') {
  process.exitCode = 17;
} else if (scenario === 'missing-html') {
  fs.writeFileSync(path.join(directory, 'junit.xml'), '<testsuites><testsuite><testcase name="React: real fixture stories and interactive pagination"/><testcase name="Blazor: real fixture stories and interactive pagination"/></testsuite></testsuites>');
}