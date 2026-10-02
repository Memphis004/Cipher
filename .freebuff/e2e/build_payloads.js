const fs = require('fs');

function payload(code) {
  return JSON.stringify({
    csharpCode: code,
    className: 'Script',
    methodName: 'Main',
    isMethodBody: false,
  });
}

const files = process.argv.slice(2);
for (const n of files) {
  const code = fs.readFileSync(`.freebuff/e2e/${n}.cs`, 'utf8');
  fs.writeFileSync(`.freebuff/e2e/p_${n}.json`, payload(code));
}
console.log('payloads written:', files.join(', '));
