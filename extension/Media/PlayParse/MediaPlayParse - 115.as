/*
	115.com User-Agent fix

	115 CDN signs the direct link over the User-Agent (the "se=u,ua" field), so the
	player has to send back exactly the UA the browser used when the link was
	signed. That UA cannot be guessed here - it is whatever the user's own browser
	sends - so the userscript passes it through the link:

		potplayer://<url>&__ua= + encodeURIComponent(navigator.userAgent)

	This script decodes that parameter and strips it again, so the URL PotPlayer
	requests is byte-identical to the one the browser signed. Without the
	parameter this script does nothing at all.

	HostSetUrlUserAgentHTTP is host/session scoped, so the .ts segments inherit it.
*/

string GetTitle()
{
	return "115.com";
}

string GetVersion()
{
	return "3";
}

string GetDesc()
{
	return "https://115.com/";
}

bool PlayitemCheck(const string &in path)
{
	string url = path;

	url.MakeLower();
	return url.find("115.com") >= 0;
}

string PlayitemParse(const string &in path, dictionary &MetaData, array<dictionary> &QualityList)
{
	string url = path;

	// PotPlayer does not URL-decode protocol arguments, so the value is still
	// percent-encoded here - that is exactly what HostUrlDecode wants.
	int p = url.find("&__ua=");
	if (p < 0)
		p = url.find("?__ua=");
	if (p < 0)
		return url;

	string ua = HostUrlDecode(url.substr(p + 6));
	url = url.substr(0, p);

	if (ua != "")
		HostSetUrlUserAgentHTTP(url, ua);

	return url;
}
