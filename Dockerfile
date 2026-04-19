FROM busybox
COPY . /amiiboapi
RUN [ "rm", "-rf", "/amiiboapi/.git" ]
RUN [ "rm", "-rf", "/amiiboapi/images" ]
RUN [ "rm", "-rf", "/amiiboapi/gameinfo_generator" ]

FROM python:3.9
EXPOSE 5000/tcp

WORKDIR /usr/src/app

COPY --from=0 /amiiboapi .
RUN [ "find", "." ]
RUN apt-get update \
    && apt-get install -y --no-install-recommends certbot cron curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*
RUN pip install --no-cache-dir -r requirements.txt
RUN chmod +x /usr/src/app/deploy/certbot/bootstrap.sh /usr/src/app/deploy/certbot/renew.sh /usr/src/app/deploy/start.sh

CMD [ "/usr/src/app/deploy/start.sh" ]
