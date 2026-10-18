# Где: menu/general
# Что видно: импорт: поведение при совпадении имён, поле для вставки, выбор файла
# Состояние: «Импорт / экспорт» в «Общих», «Импорт…»; ничего не импортируется
Reset-Ui
Open-Section 'Main_RailGeneralTitle'
Scroll-NameTo (T 'Main_ImportExportSection') 300
Start-Sleep -Milliseconds 500
Click-Key 'Main_ImportButton' | Out-Null
Start-Sleep -Seconds 2
Save-Masked 'general-import'
