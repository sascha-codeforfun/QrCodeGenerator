# The prompts that built this app

Every text prompt I gave Claude, in order — start to finish. The whole QR Code Generator (two tabs, schema-driven URL builder, vector font-glyph logos, batch ZIP export, sanitized filenames, the lot) came out of these.

---

**1.**
> can you write me a WPF app that takes a URL as input and generates a QR code from it?
> I would like to have .png and .svg as output

**2.**
> can you allow for adding a center graphic in the QR code where the center graphic is set by the user?

**3.**
> this is good as is - now I want a second tab where I can dump a list of URLs and get a zip to save with the QR svg files for them - use the sanitized URL as the svg filename, dropping protocol and subdomain completely

**4.**
> also sanitize the querystring - I want an underscore for all non a-z / 0-9 chars as a replacement

**5.**
> I would like to be able to construct the URL in the first tab from a list of parameters.
>
> the schema would be like:
>
> DOMAIN/PREFIX?PARAMLIST
>
> where paramlist is defined in a json file and the values are asked in the UI to be user-populated
> also the json shall give a default param to pre-fill the textbox where the user can change the param

**6.**
> for that center logo can we make the app use a woff file to get the vector chars from it to be used as a center graphic, since an image scales poorly?

**7.** *(with a screenshot of the running app attached)*
> the buttons to save the file should sit under the QR code - this way in full HD it all fits on the screen with no scrolling

**8.**
> can you make this shape:
>
> ```
> {
>   "name": "utm_medium",
>   "label": "Medium",
>   "default": "print"
> }
> ```
>
> accept a list of default values that then render as a dropdown?
>
> basic idea: one value = textbox, multiple = dropdown

**9.**
> now, I need a button to copy an URL crafted in tab one to be appended to the list of QR links in tab 2 for the batch process

**10.**
> de-duplication would be useful since someone might double-click the button
