// New Samsung distributor certificate for a list of TV DUIDs, KEEPING the existing author certificate
// (tizen.js createCertificate() always makes a new author too, which would break in-place upgrades).
// Uses tizen.js's own distributor steps, in the same order as createCertificate().
//
// usage: node make_distributor.js <login.json> <password-file> <outdir> <duid> [<duid> ...]
//   TIZEN_JS     path to a checkout of https://github.com/reisxd/tizen.js (default /opt/tizen.js)
//   AUTHOR_NAME  name on the certificate (default TizenNotifier); use the name your author certificate was made with
const path = require('path');
const SamsungCertificateCreator = require(path.join(process.env.TIZEN_JS || '/opt/tizen.js', 'src/samsungCertificateCreator.js'));
const { readFileSync, writeFileSync, mkdirSync } = require('fs');
(async () => {
    const [loginPath, pwPath, outDir, ...duids] = process.argv.slice(2);
    if (!duids.length) throw new Error('usage: node make_distributor.js <login.json> <password-file> <outdir> <duid> [<duid> ...]');
    const login = JSON.parse(readFileSync(loginPath, 'utf8'));
    const accessInfo = { accessToken: login.access_token, userId: login.userId };
    if (!accessInfo.accessToken || !accessInfo.userId) throw new Error('login JSON lacks access_token/userId');
    const authorInfo = { name: process.env.AUTHOR_NAME || 'TizenNotifier', email: login.inputEmailID, password: readFileSync(pwPath, 'utf8').trim(), privilegeLevel: 'Partner' };
    const c = new SamsungCertificateCreator();
    await c._downloadVDCertificates();
    const dist = c._generateDistributorCert(authorInfo, duids);
    const xml = await c._fetchDistributorCert(accessInfo, authorInfo, dist);
    const crt = await c._fetchDistributorCert(accessInfo, authorInfo, dist);
    const p12 = await c._generateDistributorPKCS12(dist, crt, authorInfo);
    mkdirSync(outDir, { recursive: true, mode: 0o700 });
    writeFileSync(`${outDir}/distributor.p12`, p12, { encoding: 'binary', mode: 0o600 });
    writeFileSync(`${outDir}/device-profile.xml`, xml, { mode: 0o600 });
    console.log('distributor certificate for', duids.length, 'TVs written to', outDir);
})().catch(e => { console.error('ERROR:', e.message); process.exit(1); });
